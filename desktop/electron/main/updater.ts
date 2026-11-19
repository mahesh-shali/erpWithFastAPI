import type { EventEmitter } from 'node:events';
import type { UpdateStatus } from '../shared/contract';

/** The part of electron-updater's AppUpdater this service uses (an interface, so tests can pass a fake). */
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
export class UpdateService {
  private status: UpdateStatus;
  private timers: ReturnType<typeof setTimeout>[] = [];

  constructor(
    private readonly updater: Updater | null,
    private readonly broadcast: (status: UpdateStatus) => void,
    unsupportedReason = 'Updates are available in the installed app.',
  ) {
    this.status = updater ? { state: 'idle' } : { state: 'unsupported', reason: unsupportedReason };
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
    this.set({ state: 'downloading', version: this.status.version, percent: 0 });
    try {
      await this.updater.downloadUpdate();
    } catch (err) {
      this.set({ state: 'error', message: friendlyError(err) });
    }
    return this.status;
  }

  install(): void {
    if (!this.updater || this.status.state !== 'downloaded') return;
    // Not silent: the installer shows its progress. Then the new version starts.
    this.updater.quitAndInstall(false, true);
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
  if (/latest\.yml|404|Unable to find latest version|No published versions/i.test(msg)) return 'No published version was found yet.';
  const line = msg.split('\n')[0].trim();
  return line.length > 200 ? `${line.slice(0, 197)}…` : line || 'Update failed.';
}
