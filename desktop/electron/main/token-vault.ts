import { app, safeStorage } from 'electron';
import { promises as fs } from 'node:fs';
import path from 'node:path';
import type { RefreshTokenStore } from './api-client';

/**
 * Keeps the refresh token between runs, encrypted with the OS key store
 * (Windows DPAPI, macOS Keychain, Linux libsecret/kwallet) through Electron's safeStorage.
 *
 * If the OS has no real key store (Linux "basic_text" backend, no keyring), nothing is written to disk:
 * the token stays in memory and the user signs in again after restarting the app.
 */
export class TokenVault implements RefreshTokenStore {
  private memory: string | null = null;
  private readonly file = path.join(app.getPath('userData'), 'session.bin');

  get persistent(): boolean {
    if (!safeStorage.isEncryptionAvailable()) return false;
    const backend = process.platform === 'linux' ? safeStorage.getSelectedStorageBackend?.() : undefined;
    return backend !== 'basic_text';
  }

  async load(): Promise<string | null> {
    if (this.memory) return this.memory;
    if (!this.persistent) return null;
    try {
      this.memory = safeStorage.decryptString(await fs.readFile(this.file));
      return this.memory;
    } catch {
      return null; // missing, or encrypted by another OS user / machine
    }
  }

  async save(token: string): Promise<void> {
    this.memory = token;
    if (!this.persistent) return;
    await fs.mkdir(path.dirname(this.file), { recursive: true });
    await fs.writeFile(this.file, safeStorage.encryptString(token), { mode: 0o600 });
  }

  async clear(): Promise<void> {
    this.memory = null;
    await fs.rm(this.file, { force: true });
  }
}
