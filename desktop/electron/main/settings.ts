import { app } from 'electron';
import { promises as fs } from 'node:fs';
import path from 'node:path';
import type { DesktopSettings, ErpEnvironment } from '../shared/contract';
import { ENVIRONMENTS } from './environment';

const LOOPBACK = new Set(['localhost', '127.0.0.1', '[::1]']);

/**
 * Normalises and validates a server URL. HTTPS is required except for loopback addresses, so tokens never
 * cross a network in clear text. ERP_ALLOW_INSECURE_HTTP=1 lifts that for a trusted LAN test server.
 */
export function normalizeServerUrl(input: string, allowInsecure = process.env.ERP_ALLOW_INSECURE_HTTP === '1'): string {
  let url: URL;
  try {
    url = new URL(String(input).trim());
  } catch {
    throw new Error('Enter a full URL, e.g. https://erp.example.com');
  }
  if (url.protocol !== 'https:' && url.protocol !== 'http:') throw new Error('The server URL must start with https://');
  if (url.protocol === 'http:' && !LOOPBACK.has(url.hostname) && !allowInsecure) {
    throw new Error('Use https:// for remote servers (http:// is only allowed for localhost)');
  }
  if (url.username || url.password) throw new Error('The server URL must not contain credentials');
  url.hash = '';
  url.search = '';
  return url.toString().replace(/\/+$/, '');
}

/**
 * The server URL starts at the mode's default (ENVIRONMENTS). ERP_SERVER_URL locks it; otherwise a URL
 * chosen on the login screen is saved in this mode's own profile (each mode has its own userData).
 */
export class SettingsStore {
  private serverUrl: string;
  private readonly file = path.join(app.getPath('userData'), 'settings.json');
  readonly locked = Boolean(process.env.ERP_SERVER_URL);

  constructor(readonly environment: ErpEnvironment) {
    this.serverUrl = ENVIRONMENTS[environment].serverUrl;
  }

  async load(): Promise<void> {
    if (this.locked) {
      this.serverUrl = normalizeServerUrl(process.env.ERP_SERVER_URL!);
      return;
    }
    try {
      const saved = JSON.parse(await fs.readFile(this.file, 'utf8')) as Partial<DesktopSettings>;
      if (saved.serverUrl) this.serverUrl = normalizeServerUrl(saved.serverUrl);
    } catch {
      /* first run or unreadable file: keep the default */
    }
  }

  get(): DesktopSettings {
    return { environment: this.environment, serverUrl: this.serverUrl, serverUrlLocked: this.locked };
  }

  get url(): string {
    return this.serverUrl;
  }

  /** Returns true if the URL actually changed. */
  async setServerUrl(input: string): Promise<boolean> {
    if (this.locked) throw new Error('The server URL is set by ERP_SERVER_URL');
    const next = normalizeServerUrl(input);
    if (next === this.serverUrl) return false;
    this.serverUrl = next;
    await fs.mkdir(path.dirname(this.file), { recursive: true });
    await fs.writeFile(this.file, JSON.stringify({ serverUrl: next }, null, 2));
    return true;
  }
}
