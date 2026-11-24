import { appendFileSync, mkdirSync, renameSync, statSync } from 'node:fs';
import type { EventEmitter } from 'node:events';
import path from 'node:path';
import type { UpdateStatus } from '../shared/contract';

/**
 * The part of electron-updater's AppUpdater this service uses (an interface, so tests can pass a fake).
 * On Windows, SquirrelUpdater (squirrel.ts) implements it over Electron's built-in autoUpdater.
 */
export interface Updater extends Pick<EventEmitter, 'on'> {
  autoDownload: boolean;
  autoInstallOnAppQuit: boolean;
  checkForUpdates(): Promise<unknown>;
  downloadUpdate(): Promise<unknown>;
  quitAndInstall(isSilent?: boolean, isForceRunAfter?: boolean): void;
}

interface ReleaseInfo { version: string; releaseNotes?: string | { note: string | null }[] | null }

export const CHECK_DELAY_MS = 10_000;           // first check shortly after start, not during it
export const CHECK_INTERVAL_MS = 6 * 3600_000;  // then every 6 hours while the app is open

/**
 * Updates from GitHub Releases. Checks in the background and shows what it found; the download only starts
 * when the user asks for it. A downloaded update installs on "Restart now" or when the app next quits.
 */
export interface UpdateServiceOptions {
  unsupportedReason?: string;
  log?: UpdateLogger;
  /** Runs right before the app quits to install (e.g. puts the update splash on screen). Never blocks forever. */
  beforeInstall?: (version: string) => Promise<unknown>;
}

export class UpdateService {
  private status: UpdateStatus;
  private timers: ReturnType<typeof setTimeout>[] = [];
  private installing = false;
  private readonly log?: UpdateLogger;
  private readonly beforeInstall?: (version: string) => Promise<unknown>;

  constructor(
    private readonly updater: Updater | null,
    private readonly broadcast: (status: UpdateStatus) => void,
    options: UpdateServiceOptions = {},
  ) {
    this.log = options.log;
    this.beforeInstall = options.beforeInstall;
    this.status = updater
      ? { state: 'idle' }
      : { state: 'unsupported', reason: options.unsupportedReason ?? 'Updates are available in the installed app.' };
    if (!updater) return;

    updater.autoDownload = false;          // the user decides when to download
    updater.autoInstallOnAppQuit = true;   // a downloaded update is applied at the next quit at the latest
    updater.on('checking-for-update', () => this.set({ state: 'checking' }));
    updater.on('update-not-available', () => this.set({ state: 'up-to-date', checkedAt: new Date().toISOString() }));
    updater.on('update-available', (info: ReleaseInfo) =>
      this.set({ state: 'available', version: info.version, releaseNotes: notesToText(info.releaseNotes) }));
    updater.on('download-progress', (p: { percent: number }) => {
      const version = 'version' in this.status ? this.status.version : '';
      this.set({ state: 'downloading', version, percent: Math.max(0, Math.min(100, Math.round(p.percent))) });
    });
    updater.on('update-downloaded', (info: ReleaseInfo) => this.set({ state: 'downloaded', version: info.version }));
    updater.on('error', (err: unknown) => this.set({ state: 'error', message: friendlyError(err) }));
  }

  get(): UpdateStatus {
    return this.status;
  }

  /** Starts the background checks (first one after CHECK_DELAY_MS). */
  start(): void {
    if (!this.updater) return;
    this.timers.push(setTimeout(() => void this.check().catch(() => undefined), CHECK_DELAY_MS));
    this.timers.push(setInterval(() => {
      // Don't interrupt a download, or replace a finished one with a new check.
      if (this.status.state !== 'downloading' && this.status.state !== 'downloaded') void this.check().catch(() => undefined);
    }, CHECK_INTERVAL_MS));
  }

  stop(): void {
    this.timers.forEach((t) => clearTimeout(t));
    this.timers = [];
  }

  async check(): Promise<UpdateStatus> {
    if (!this.updater) return this.status;
    if (this.status.state === 'downloading' || this.status.state === 'downloaded') return this.status;
    try {
      await this.updater.checkForUpdates();
    } catch (err) {
      this.set({ state: 'error', message: friendlyError(err) });
    }
    return this.status;
  }

  async download(): Promise<UpdateStatus> {
    if (!this.updater || this.status.state !== 'available') return this.status;
    this.set({ state: 'downloading', version: this.status.version, percent: null }); // until a progress event
    try {
      await this.updater.downloadUpdate();
    } catch (err) {
      this.set({ state: 'error', message: friendlyError(err) });
    }
    return this.status;
  }

  async install(): Promise<void> {
    if (!this.updater || this.status.state !== 'downloaded' || this.installing) return;
    this.installing = true; // a second click must not start a second installer
    const { version } = this.status;
    this.log?.info(`Restart now: installing ${version} silently, then relaunching`);
    try {
      const shown = await this.beforeInstall?.(version);
      this.log?.info(`Update splash shown: ${shown ?? 'n/a'}`);
    } catch (err) {
      this.log?.warn(`Update splash failed: ${friendlyError(err)}`); // the update still goes ahead
    }
    // electron-updater (macOS/Linux): install silently, then start the new version, which reopens where the user
    // left off. SquirrelUpdater ignores the flags: Squirrel always restarts into the staged version.
    this.updater.quitAndInstall(true, true);
  }

  private set(next: UpdateStatus): void {
    this.status = next;
    this.broadcast(next);
  }
}

/** GitHub release notes arrive as HTML; the renderer only ever gets plain text. */
export function notesToText(notes: ReleaseInfo['releaseNotes']): string | null {
  if (!notes) return null;
  const raw = typeof notes === 'string' ? notes : notes.map((n) => n.note ?? '').join('\n');
  const text = raw
    .replace(/<br\s*\/?>/gi, '\n').replace(/<\/(p|li|h\d)>/gi, '\n').replace(/<li[^>]*>/gi, '• ')
    .replace(/<[^>]+>/g, '')
    .replace(/&nbsp;/g, ' ').replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&#39;/g, "'")
    .replace(/&amp;/g, '&')
    .replace(/\n{3,}/g, '\n\n').trim();
  return text ? text.slice(0, 2000) : null;
}

/** electron-updater errors can carry whole HTTP responses; show one short line. */
export function friendlyError(err: unknown): string {
  const msg = err instanceof Error ? err.message : String(err);
  if (/ENOTFOUND|ECONNREFUSED|ETIMEDOUT|ECONNRESET|net::ERR_/i.test(msg)) return 'Could not reach the update server. Check your internet connection.';
  if (/latest\.yml|RELEASES|404|Unable to find latest version|No published versions/i.test(msg)) return 'No published version was found yet.';
  const line = msg.split('\n')[0].trim();
  return line.length > 200 ? `${line.slice(0, 197)}…` : line || 'Update failed.';
}

export interface UpdateLogger {
  info(message?: unknown): void;
  warn(message?: unknown): void;
  error(message?: unknown): void;
  debug?(message: string): void;
}

/**
 * Log for electron-updater (it records each check, download and the exact installer command it runs), so an
 * update problem can be diagnosed afterwards. Rolls over to updater.old.log at `maxBytes`. Never throws.
 */
export function fileLogger(file: string, maxBytes = 1024 * 1024): UpdateLogger {
  const write = (level: string, message: unknown) => {
    try {
      mkdirSync(path.dirname(file), { recursive: true });
      try {
        if (statSync(file).size > maxBytes) renameSync(file, file.replace(/\.log$/, '.old.log'));
      } catch { /* no file yet */ }
      const text = message instanceof Error ? (message.stack ?? message.message) : String(message);
      appendFileSync(file, `${new Date().toISOString()} [${level}] ${text}\n`);
    } catch {
      /* logging must never break updating */
    }
  };
  return {
    info: (m) => write('info', m),
    warn: (m) => write('warn', m),
    error: (m) => write('error', m),
    debug: (m) => write('debug', m),
  };
}
