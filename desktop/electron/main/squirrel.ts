import { spawn } from 'node:child_process';
import { EventEmitter } from 'node:events';
import { existsSync } from 'node:fs';
import path from 'node:path';
import type { Updater, UpdateLogger } from './updater';

/**
 * Squirrel.Windows support. The installer (Setup.exe) unpacks the app per user into
 *   %LocalAppData%\erp-desktop\  Update.exe, ERP Platform.exe (stub), app-<version>\, packages\
 * and runs the app with --squirrel-* switches on install, update and uninstall. Updates are applied by Update.exe
 * into a new app-<version> folder; the stub always starts the newest one.
 */

/** Update.exe sits one folder above the running app-<version> folder. */
export function updateExePath(execPath: string): string {
  return path.resolve(path.dirname(execPath), '..', 'Update.exe');
}

/** True when this copy was installed by Squirrel (so it can update itself). */
export function isSquirrelInstall(execPath: string, exists: (p: string) => boolean = existsSync): boolean {
  return exists(updateExePath(execPath));
}

export type SquirrelEvent = 'install' | 'updated' | 'uninstall' | 'obsolete' | 'firstrun';

export function squirrelEvent(argv: readonly string[]): SquirrelEvent | null {
  const m = /^--squirrel-(install|updated|uninstall|obsolete|firstrun)$/.exec(argv[1] ?? '');
  return m ? (m[1] as SquirrelEvent) : null;
}

/**
 * Handles the lifecycle switches Squirrel starts the app with. Returns true when the app must quit right away
 * instead of starting (install/update/uninstall run with a ~15 s budget and no UI). --squirrel-firstrun starts normally.
 */
export function handleSquirrelEvent(
  argv: readonly string[],
  execPath: string,
  quit: () => void,
  run: (exe: string, args: string[]) => { on(event: 'close' | 'error', cb: () => void): unknown } = (exe, args) =>
    spawn(exe, args, { detached: true, stdio: 'ignore', windowsHide: true }),
): boolean {
  const event = squirrelEvent(argv);
  if (!event || event === 'firstrun') return false;
  if (event === 'obsolete') {
    quit(); // an older app-<version> folder being retired after an update
    return true;
  }
  const exe = path.basename(execPath);
  // Start-menu and desktop shortcuts point at the stub, so they keep working across updates.
  const args = event === 'uninstall' ? [`--removeShortcut=${exe}`] : [`--createShortcut=${exe}`];
  let done = false;
  const finish = () => { if (!done) { done = true; quit(); } };
  try {
    const child = run(updateExePath(execPath), args);
    child.on('close', finish);
    child.on('error', finish);
  } catch {
    finish();
  }
  setTimeout(finish, 10_000).unref?.(); // never outlive Squirrel's budget
  return true;
}

/** The part of Electron's built-in autoUpdater (Squirrel.Windows) this adapter uses. */
export interface NativeAutoUpdater {
  setFeedURL(options: { url: string }): void;
  checkForUpdates(): void;
  quitAndInstall(): void;
  on(event: string, listener: (...args: any[]) => void): unknown;
  removeListener(event: string, listener: (...args: any[]) => void): unknown;
}

export interface SquirrelUpdaterOptions {
  /** Folder that serves RELEASES and the .nupkg files, e.g. https://github.com/<owner>/<repo>/releases/latest/download */
  feedUrl: string;
  currentVersion: string;
  fetchText: (url: string) => Promise<string>;
  /** Optional: GitHub "latest release" API URL, used only for release notes. */
  releaseApiUrl?: string;
  log?: UpdateLogger;
}

/**
 * Makes Electron's Squirrel autoUpdater look like electron-updater to UpdateService, so the update button and panel
 * stay the same: checking only reads RELEASES (no download), and the download starts when the user asks for it.
 * Squirrel reports no download progress, so no 'download-progress' events are emitted.
 */
export class SquirrelUpdater extends EventEmitter implements Updater {
  autoDownload = false;          // kept for the Updater interface; downloads only ever start from downloadUpdate()
  autoInstallOnAppQuit = true;   // Squirrel: once downloaded, the next start runs the new version anyway
  private available: string | null = null;
  private downloading: Promise<unknown> | null = null;

  constructor(private readonly native: NativeAutoUpdater, private readonly opts: SquirrelUpdaterOptions) {
    super();
    native.setFeedURL({ url: opts.feedUrl });
    // An 'error' with no listener would throw in the main process.
    native.on('error', (err: unknown) => opts.log?.warn(`Squirrel: ${err instanceof Error ? err.message : String(err)}`));
  }

  async checkForUpdates(): Promise<unknown> {
    this.emit('checking-for-update');
    const feed = this.opts.feedUrl.replace(/\/+$/, '');
    const releases = await this.opts.fetchText(`${feed}/RELEASES`);
    const latest = latestVersion(releases);
    this.opts.log?.info(`Squirrel: installed ${this.opts.currentVersion}, latest published ${latest ?? 'none'}`);
    if (!latest) throw new Error('No published versions in RELEASES');
    if (compareVersions(latest, this.opts.currentVersion) <= 0) {
      this.available = null;
      this.emit('update-not-available', { version: latest });
      return null;
    }
    this.available = latest;
    this.emit('update-available', { version: latest, releaseNotes: await this.releaseNotes() });
    return { version: latest };
  }

  downloadUpdate(): Promise<unknown> {
    if (this.downloading) return this.downloading;
    const version = this.available;
    this.downloading = new Promise((resolve, reject) => {
      const cleanup = () => {
        this.native.removeListener('update-downloaded', onDownloaded);
        this.native.removeListener('update-not-available', onNone);
        this.native.removeListener('error', onError);
        this.downloading = null;
      };
      const onDownloaded = () => {
        cleanup();
        this.opts.log?.info(`Squirrel: ${version} downloaded and staged`);
        this.emit('update-downloaded', { version });
        resolve({ version });
      };
      const onNone = () => { cleanup(); reject(new Error('The update is no longer available.')); };
      const onError = (err: unknown) => { cleanup(); reject(err instanceof Error ? err : new Error(String(err))); };
      this.native.on('update-downloaded', onDownloaded);
      this.native.on('update-not-available', onNone);
      this.native.on('error', onError);
      this.opts.log?.info(`Squirrel: downloading ${version}`);
      this.native.checkForUpdates(); // Squirrel checks and downloads in one step
    });
    return this.downloading;
  }

  /** Squirrel restarts into the staged version on its own; the electron-updater flags don't apply. */
  quitAndInstall(): void {
    this.native.quitAndInstall();
  }

  private async releaseNotes(): Promise<string | null> {
    if (!this.opts.releaseApiUrl) return null;
    try {
      const body = (JSON.parse(await this.opts.fetchText(this.opts.releaseApiUrl)) as { body?: unknown }).body;
      return typeof body === 'string' ? body : null;
    } catch {
      return null; // notes are nice to have; never fail a check over them
    }
  }
}

/**
 * Highest version listed in a Squirrel RELEASES file. Each line is `<sha1> <name>-<version>-full|delta.nupkg <size>`.
 */
export function latestVersion(releases: string): string | null {
  let best: string | null = null;
  for (const line of releases.split(/\r?\n/)) {
    const file = line.trim().split(/\s+/)[1];
    const m = file && /-(\d+\.\d+\.\d+(?:-[0-9A-Za-z.]+)?)-(?:full|delta)\.nupkg$/i.exec(file);
    if (m && (!best || compareVersions(m[1], best) > 0)) best = m[1];
  }
  return best;
}

/** Semver-ish compare: numeric x.y.z, and a pre-release (1.2.0-beta) sorts before its release. */
export function compareVersions(a: string, b: string): number {
  const [coreA, preA] = a.split('-', 2);
  const [coreB, preB] = b.split('-', 2);
  const pa = coreA.split('.').map(Number);
  const pb = coreB.split('.').map(Number);
  for (let i = 0; i < 3; i++) {
    const d = (pa[i] || 0) - (pb[i] || 0);
    if (d !== 0) return Math.sign(d);
  }
  if (preA && !preB) return -1;
  if (!preA && preB) return 1;
  return preA && preB ? Math.sign(preA.localeCompare(preB)) : 0;
}
