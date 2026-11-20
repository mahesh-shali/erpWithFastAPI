/**
 * Keeps the unlocked chat key on this device so the passphrase is asked once, not on every visit.
 * The private key is a NON-extractable CryptoKey stored in IndexedDB: page scripts can use it but
 * can't read its bytes. It is deleted on sign-out. Falls back to memory when IndexedDB isn't available.
 */

export interface DeviceKey { userId: string; keyId: string; publicKey: string; privateKey: CryptoKey }

const DB = 'erp-secure-chat';
const STORE = 'keys';
const memory = new Map<string, DeviceKey>();

function openDb(): Promise<IDBDatabase | null> {
  if (typeof indexedDB === 'undefined') return Promise.resolve(null);
  return new Promise((resolve) => {
    try {
      const req = indexedDB.open(DB, 1);
      req.onupgradeneeded = () => req.result.createObjectStore(STORE, { keyPath: 'userId' });
      req.onsuccess = () => resolve(req.result);
      req.onerror = () => resolve(null);
    } catch {
      resolve(null);
    }
  });
}

async function run<T>(mode: IDBTransactionMode, fn: (s: IDBObjectStore) => IDBRequest<T>): Promise<T | undefined> {
  const db = await openDb();
  if (!db) return undefined;
  try {
    return await new Promise<T | undefined>((resolve) => {
      const tx = db.transaction(STORE, mode);
      const req = fn(tx.objectStore(STORE));
      tx.oncomplete = () => resolve(req.result);
      tx.onerror = () => resolve(undefined);
      tx.onabort = () => resolve(undefined);
    });
  } catch {
    return undefined;
  } finally {
    db.close();
  }
}

export async function loadDeviceKey(userId: string): Promise<DeviceKey | null> {
  const stored = await run<DeviceKey>('readonly', (s) => s.get(userId) as IDBRequest<DeviceKey>);
  return stored ?? memory.get(userId) ?? null;
}

export async function saveDeviceKey(key: DeviceKey): Promise<void> {
  memory.set(key.userId, key);
  await run('readwrite', (s) => s.put(key));
}

/** Forgets the key of one user, or of everyone on this device (sign-out). */
export async function forgetDeviceKeys(userId?: string): Promise<void> {
  if (userId) memory.delete(userId); else memory.clear();
  await run('readwrite', (s) => (userId ? s.delete(userId) : s.clear()));
}
