// @vitest-environment node
import { describe, expect, it } from 'vitest';
import {
  changePassphrase, createIdentity, decryptMessage, encryptMessage, fingerprint, fromB64, toB64, unlockIdentity,
  UndecryptableError, WrongPassphraseError, type UnlockedKey,
} from './crypto';

const FAST = 1_000; // PBKDF2 iterations for tests only (the app uses 600k)
const CONV = 'c0ffee00-0000-4000-8000-000000000001';

async function person(name: string, pass = `${name}-passphrase`) {
  const { backup, privateKey } = await createIdentity(pass, FAST);
  const key: UnlockedKey = { keyId: `${name}-key`, publicKey: backup.publicKey, privateKey };
  return { backup, key, pass };
}

async function send(from: UnlockedKey, to: UnlockedKey[], text: string, conversationId = CONV) {
  const sealed = await encryptMessage(from, conversationId, { text }, [from, ...to]);
  // What a recipient gets back from GET /messages: their own wrapped key + the sender's public key
  return (recipient: UnlockedKey, overrides: Partial<{ conversationId: string; ciphertext: string; senderKeyId: string }> = {}) => ({
    conversationId, senderKeyId: from.keyId, senderPublicKey: from.publicKey,
    ciphertext: sealed.ciphertext, iv: sealed.iv,
    wrappedKey: sealed.keys.find((k) => k.recipientKeyId === recipient.keyId)?.wrappedKey ?? null,
    ...overrides,
  });
}

describe('secure chat crypto', () => {
  it('base64 round-trips binary data', () => {
    const bytes = new Uint8Array(70_000).map((_, i) => i % 256);
    expect(fromB64(toB64(bytes))).toEqual(bytes);
  });

  it('every recipient (and the sender) can read a message; each gets a different wrapped key', async () => {
    const alice = await person('alice');
    const bob = await person('bob');
    const carol = await person('carol');
    const sealed = await encryptMessage(alice.key, CONV, { text: 'Hello team 👋' }, [alice.key, bob.key, carol.key]);
    expect(new Set(sealed.keys.map((k) => k.wrappedKey)).size).toBe(3);
    expect(sealed.ciphertext).not.toContain('Hello');

    const asSeenBy = await send(alice.key, [bob.key, carol.key], 'Hello team 👋');
    for (const who of [alice.key, bob.key, carol.key])
      expect(await decryptMessage(who, asSeenBy(who))).toEqual({ text: 'Hello team 👋' });
  });

  it('someone who was not a recipient cannot read it, even with another recipient\'s wrapped key', async () => {
    const alice = await person('alice');
    const bob = await person('bob');
    const eve = await person('eve');
    const asSeenBy = await send(alice.key, [bob.key], 'secret');
    expect(asSeenBy(eve.key).wrappedKey).toBeNull();
    await expect(decryptMessage(eve.key, asSeenBy(eve.key))).rejects.toBeInstanceOf(UndecryptableError);
    await expect(decryptMessage(eve.key, { ...asSeenBy(bob.key) })).rejects.toBeInstanceOf(UndecryptableError);
  });

  it('detects tampering and messages moved to another conversation or sender key', async () => {
    const alice = await person('alice');
    const bob = await person('bob');
    const asSeenBy = await send(alice.key, [bob.key], 'pay invoice 42');
    const ct = fromB64(asSeenBy(bob.key).ciphertext);
    ct[0] ^= 1;
    await expect(decryptMessage(bob.key, asSeenBy(bob.key, { ciphertext: toB64(ct) }))).rejects.toBeInstanceOf(UndecryptableError);
    await expect(decryptMessage(bob.key, asSeenBy(bob.key, { conversationId: 'another-conversation' }))).rejects.toBeInstanceOf(UndecryptableError);
    await expect(decryptMessage(bob.key, asSeenBy(bob.key, { senderKeyId: 'bob-key' }))).rejects.toBeInstanceOf(UndecryptableError);
  });

  it('a substituted sender public key fails to decrypt', async () => {
    const alice = await person('alice');
    const bob = await person('bob');
    const mallory = await person('mallory');
    const asSeenBy = await send(alice.key, [bob.key], 'hi');
    await expect(decryptMessage(bob.key, { ...asSeenBy(bob.key), senderPublicKey: mallory.key.publicKey }))
      .rejects.toBeInstanceOf(UndecryptableError);
  });

  it('the key backup only opens with the right passphrase, and the unlocked key works', async () => {
    const alice = await person('alice', 'correct horse battery');
    const bob = await person('bob');
    await expect(unlockIdentity(alice.backup, 'wrong passphrase')).rejects.toBeInstanceOf(WrongPassphraseError);

    // e.g. signing in on the desktop app: unlock the same identity from the server backup
    const restored: UnlockedKey = { ...alice.key, privateKey: await unlockIdentity(alice.backup, 'correct horse battery') };
    expect(restored.privateKey.extractable).toBe(false);
    const asSeenBy = await send(bob.key, [alice.key], 'from bob');
    expect(await decryptMessage(restored, asSeenBy(alice.key))).toEqual({ text: 'from bob' });
  });

  it('a backup cannot be paired with a different public key', async () => {
    const alice = await person('alice');
    const bob = await person('bob');
    await expect(unlockIdentity({ ...alice.backup, publicKey: bob.backup.publicKey }, alice.pass)).rejects.toBeInstanceOf(WrongPassphraseError);
  });

  it('changing the passphrase keeps the same key', async () => {
    const alice = await person('alice', 'old passphrase');
    const updated = await changePassphrase(alice.backup, 'old passphrase', 'new passphrase', FAST);
    expect(updated.publicKey).toBe(alice.backup.publicKey);
    expect(updated.kdfSalt).not.toBe(alice.backup.kdfSalt);
    await expect(unlockIdentity(updated, 'old passphrase')).rejects.toBeInstanceOf(WrongPassphraseError);
    await expect(unlockIdentity(updated, 'new passphrase')).resolves.toBeDefined();
    await expect(changePassphrase(alice.backup, 'not it', 'x')).rejects.toBeInstanceOf(WrongPassphraseError);
  });

  it('messages for a previous key say why they cannot be read', async () => {
    const alice = await person('alice');
    await expect(decryptMessage(alice.key, {
      conversationId: CONV, senderKeyId: 'x', senderPublicKey: alice.key.publicKey, ciphertext: '', iv: '', wrappedKey: null,
    })).rejects.toThrow(/before your current key/);
  });

  it('fingerprints are stable and differ between keys', async () => {
    const alice = await person('alice');
    const bob = await person('bob');
    const fa = await fingerprint(alice.key.publicKey);
    expect(fa).toMatch(/^([0-9a-f]{4} ){15}[0-9a-f]{4}$/);
    expect(await fingerprint(alice.key.publicKey)).toBe(fa);
    expect(await fingerprint(bob.key.publicKey)).not.toBe(fa);
  });
});
