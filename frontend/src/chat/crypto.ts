/**
 * End-to-end encryption for Messages. WebCrypto only (no dependencies); identical in the web and desktop apps.
 *
 * Identity: each user has one ECDH P-256 key pair. The private key leaves the device only encrypted with
 * AES-256-GCM under a key derived from the user's passphrase (PBKDF2-SHA256, 600k iterations), so the
 * server stores a backup it cannot open and the same identity works in the browser and the desktop app.
 * After unlocking, the private key is kept as a NON-extractable CryptoKey.
 *
 * Messages: a fresh random AES-256-GCM key encrypts the payload. That key is wrapped once per recipient key:
 *   ECDH(sender private, recipient public) -> HKDF-SHA256(info = version|senderKeyId|recipientKeyId) -> AES-GCM
 * The sender is always one of the recipients, so they can read their own history.
 * Associated data binds every ciphertext to its conversation and sender key, so the server can't move a
 * message to another conversation or attribute it to another key without decryption failing.
 *
 * What the server can still see: who talks to whom and when, and message sizes. It could also hand out a
 * fake public key; users can compare key fingerprints (fingerprint()) out of band to rule that out.
 */

const VERSION = 'erp-chat/v1';
export const KDF_ITERATIONS = 600_000;
const EC: EcKeyImportParams = { name: 'ECDH', namedCurve: 'P-256' };
const IV_BYTES = 12;

const subtle = () => globalThis.crypto.subtle;
const te = new TextEncoder();
const td = new TextDecoder();

export class WrongPassphraseError extends Error {
  constructor() { super('That passphrase is not correct.'); }
}
export class UndecryptableError extends Error {
  constructor(message = 'This message could not be decrypted.') { super(message); }
}

/** What the server stores for a user: public key + the passphrase-encrypted private key. */
export interface KeyBackup { publicKey: string; encryptedPrivateKey: string; kdfSalt: string; kdfIterations: number }
/** An unlocked identity on this device. */
export interface UnlockedKey { keyId: string; publicKey: string; privateKey: CryptoKey }
export interface Recipient { keyId: string; publicKey: string }
export interface MessagePayload { text: string }
export interface SealedMessage { ciphertext: string; iv: string; keys: { recipientKeyId: string; wrappedKey: string }[] }
export interface EncryptedMessage {
  conversationId: string; senderKeyId: string; senderPublicKey: string;
  ciphertext: string; iv: string; wrappedKey: string | null;
}

// ---------------- encoding ----------------

export function toB64(data: ArrayBuffer | Uint8Array): string {
  const bytes = data instanceof Uint8Array ? data : new Uint8Array(data);
  let s = '';
  for (let i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(s);
}

export function fromB64(s: string): Uint8Array {
  const bin = atob(s);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}

const random = (n: number) => globalThis.crypto.getRandomValues(new Uint8Array(n));

function concat(a: Uint8Array, b: Uint8Array): Uint8Array {
  const out = new Uint8Array(a.length + b.length);
  out.set(a);
  out.set(b, a.length);
  return out;
}

// ---------------- identity ----------------

async function passphraseKey(passphrase: string, salt: Uint8Array, iterations: number): Promise<CryptoKey> {
  const base = await subtle().importKey('raw', te.encode(passphrase.normalize('NFKC')), 'PBKDF2', false, ['deriveKey']);
  return subtle().deriveKey(
    { name: 'PBKDF2', hash: 'SHA-256', salt, iterations }, base, { name: 'AES-GCM', length: 256 }, false, ['encrypt', 'decrypt']);
}

const backupAad = (publicKey: string) => te.encode(`${VERSION}|backup|${publicKey}`);

async function seal(pkcs8: ArrayBuffer, publicKey: string, passphrase: string, iterations: number): Promise<KeyBackup> {
  const salt = random(16);
  const iv = random(IV_BYTES);
  const key = await passphraseKey(passphrase, salt, iterations);
  // The public key is associated data: a backup only opens together with its own public key.
  const ct = await subtle().encrypt({ name: 'AES-GCM', iv, additionalData: backupAad(publicKey) }, key, pkcs8);
  return { publicKey, encryptedPrivateKey: toB64(concat(iv, new Uint8Array(ct))), kdfSalt: toB64(salt), kdfIterations: iterations };
}

async function openBackup(backup: KeyBackup, passphrase: string): Promise<ArrayBuffer> {
  const data = fromB64(backup.encryptedPrivateKey);
  const key = await passphraseKey(passphrase, fromB64(backup.kdfSalt), backup.kdfIterations);
  try {
    return await subtle().decrypt(
      { name: 'AES-GCM', iv: data.subarray(0, IV_BYTES), additionalData: backupAad(backup.publicKey) }, key, data.subarray(IV_BYTES));
  } catch {
    throw new WrongPassphraseError();
  }
}

const importPrivate = (pkcs8: ArrayBuffer) => subtle().importKey('pkcs8', pkcs8, EC, false, ['deriveBits']);

/** New identity. Returns the backup to upload and the (non-extractable) private key for this device. */
export async function createIdentity(passphrase: string, iterations = KDF_ITERATIONS): Promise<{ backup: KeyBackup; privateKey: CryptoKey }> {
  const pair = (await subtle().generateKey(EC, true, ['deriveBits'])) as CryptoKeyPair;
  const publicKey = toB64(await subtle().exportKey('spki', pair.publicKey));
  const pkcs8 = await subtle().exportKey('pkcs8', pair.privateKey);
  const backup = await seal(pkcs8, publicKey, passphrase, iterations);
  return { backup, privateKey: await importPrivate(pkcs8) };
}

/** Opens the server backup with the passphrase. Throws WrongPassphraseError. */
export async function unlockIdentity(backup: KeyBackup, passphrase: string): Promise<CryptoKey> {
  return importPrivate(await openBackup(backup, passphrase));
}

/** Same key, new passphrase. */
export async function changePassphrase(backup: KeyBackup, oldPassphrase: string, newPassphrase: string, iterations = KDF_ITERATIONS): Promise<KeyBackup> {
  return seal(await openBackup(backup, oldPassphrase), backup.publicKey, newPassphrase, iterations);
}

/** SHA-256 of the public key, as 16 groups of 4 hex digits. Compare it with the other person to verify their key. */
export async function fingerprint(publicKey: string): Promise<string> {
  const hex = [...new Uint8Array(await subtle().digest('SHA-256', fromB64(publicKey)))]
    .map((b) => b.toString(16).padStart(2, '0')).join('');
  return hex.match(/.{4}/g)!.join(' ');
}

// ---------------- messages ----------------

// Pairwise wrapping keys, cached per unlocked private key (ECDH + HKDF once per pair, not per message).
const pairCache = new WeakMap<CryptoKey, Map<string, Promise<CryptoKey>>>();

function pairKey(myPrivate: CryptoKey, otherPublic: string, senderKeyId: string, recipientKeyId: string): Promise<CryptoKey> {
  let cache = pairCache.get(myPrivate);
  if (!cache) pairCache.set(myPrivate, (cache = new Map()));
  const id = `${senderKeyId}|${recipientKeyId}|${otherPublic}`;
  let key = cache.get(id);
  if (!key) {
    key = (async () => {
      const pub = await subtle().importKey('spki', fromB64(otherPublic), EC, false, []);
      const shared = await subtle().deriveBits({ name: 'ECDH', public: pub }, myPrivate, 256);
      const hkdf = await subtle().importKey('raw', shared, 'HKDF', false, ['deriveKey']);
      return subtle().deriveKey(
        { name: 'HKDF', hash: 'SHA-256', salt: new Uint8Array(32), info: te.encode(`${VERSION}|wrap|${senderKeyId}|${recipientKeyId}`) },
        hkdf, { name: 'AES-GCM', length: 256 }, false, ['encrypt', 'decrypt']);
    })();
    const c = cache;
    key.catch(() => c.delete(id));
    cache.set(id, key);
  }
  return key;
}

const messageAad = (conversationId: string, senderKeyId: string) => te.encode(`${VERSION}|msg|${conversationId}|${senderKeyId}`);
const wrapAad = (conversationId: string) => te.encode(`${VERSION}|key|${conversationId}`);

/** Encrypts a message for every recipient key (include your own key to read it later). */
export async function encryptMessage(
  me: UnlockedKey, conversationId: string, payload: MessagePayload, recipients: Recipient[],
): Promise<SealedMessage> {
  const raw = random(32);
  try {
    const contentKey = await subtle().importKey('raw', raw, 'AES-GCM', false, ['encrypt']);
    const iv = random(IV_BYTES);
    const ct = await subtle().encrypt(
      { name: 'AES-GCM', iv, additionalData: messageAad(conversationId, me.keyId) }, contentKey,
      te.encode(JSON.stringify({ v: 1, text: payload.text })));
    const keys = await Promise.all(recipients.map(async (r) => {
      const k = await pairKey(me.privateKey, r.publicKey, me.keyId, r.keyId);
      const wiv = random(IV_BYTES);
      const wrapped = await subtle().encrypt({ name: 'AES-GCM', iv: wiv, additionalData: wrapAad(conversationId) }, k, raw);
      return { recipientKeyId: r.keyId, wrappedKey: toB64(concat(wiv, new Uint8Array(wrapped))) };
    }));
    return { ciphertext: toB64(ct), iv: toB64(iv), keys };
  } finally {
    raw.fill(0);
  }
}

/** Decrypts a message with this device's key. Throws UndecryptableError. */
export async function decryptMessage(me: UnlockedKey, m: EncryptedMessage): Promise<MessagePayload> {
  if (!m.wrappedKey) throw new UndecryptableError('Sent before your current key was set up, so it can no longer be read.');
  try {
    const k = await pairKey(me.privateKey, m.senderPublicKey, m.senderKeyId, me.keyId);
    const w = fromB64(m.wrappedKey);
    const raw = new Uint8Array(await subtle().decrypt(
      { name: 'AES-GCM', iv: w.subarray(0, IV_BYTES), additionalData: wrapAad(m.conversationId) }, k, w.subarray(IV_BYTES)));
    const contentKey = await subtle().importKey('raw', raw, 'AES-GCM', false, ['decrypt']);
    raw.fill(0);
    const plain = await subtle().decrypt(
      { name: 'AES-GCM', iv: fromB64(m.iv), additionalData: messageAad(m.conversationId, m.senderKeyId) }, contentKey, fromB64(m.ciphertext));
    const parsed = JSON.parse(td.decode(plain)) as { text?: unknown };
    if (typeof parsed.text !== 'string') throw new Error('bad payload');
    return { text: parsed.text };
  } catch {
    throw new UndecryptableError();
  }
}
