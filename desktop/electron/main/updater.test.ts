import { EventEmitter } from 'node:events';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { UpdateStatus } from '../shared/contract';
import { CHECK_DELAY_MS, friendlyError, notesToText, UpdateService, type Updater } from './updater';

class FakeUpdater extends EventEmitter implements Updater {
  autoDownload = true;
  autoInstallOnAppQuit = false;
  checkForUpdates = vi.fn(async () => { this.emit('checking-for-update'); });
  downloadUpdate = vi.fn(async () => undefined);
  quitAndInstall = vi.fn();
}

describe('UpdateService', () => {
  let updater: FakeUpdater;
  let sent: UpdateStatus[];
  let svc: UpdateService;

  beforeEach(() => {
    updater = new FakeUpdater();
    sent = [];
    svc = new UpdateService(updater, (s) => sent.push(s));
  });
  afterEach(() => { svc.stop(); vi.useRealTimers(); });

  it('lets the user decide when to download, and installs a downloaded update on quit', () => {
    expect(updater.autoDownload).toBe(false);
    expect(updater.autoInstallOnAppQuit).toBe(true);
    expect(svc.get()).toEqual({ state: 'idle' });
  });

  it('goes available → downloading → downloaded and broadcasts each step', async () => {
    await svc.check();
    updater.emit('update-available', { version: '1.2.0', releaseNotes: '<p>Faster <b>orders</b></p>' });
    expect(svc.get()).toEqual({ state: 'available', version: '1.2.0', releaseNotes: 'Faster orders' });

    await svc.download();
    expect(updater.downloadUpdate).toHaveBeenCalledOnce();
    updater.emit('download-progress', { percent: 41.6 });
    expect(svc.get()).toEqual({ state: 'downloading', version: '1.2.0', percent: 42 });

    updater.emit('update-downloaded', { version: '1.2.0' });
    expect(svc.get()).toEqual({ state: 'downloaded', version: '1.2.0' });
    expect(sent.map((s) => s.state)).toEqual(['checking', 'available', 'downloading', 'downloading', 'downloaded']);

    await svc.install();
    expect(updater.quitAndInstall).toHaveBeenCalledWith(true, true); // silent: no setup wizard, then relaunch
  });

  it('ignores download and install when there is nothing to download or install', async () => {
    await svc.download();
    await svc.install();
    expect(updater.downloadUpdate).not.toHaveBeenCalled();
    expect(updater.quitAndInstall).not.toHaveBeenCalled();
  });

  it('shows the update splash before quitting to install, and installs only once', async () => {
    const order: string[] = [];
    const splashSvc = new UpdateService(updater, () => undefined, {
      beforeInstall: async (v) => { order.push(`splash ${v}`); return true; },
    });
    updater.quitAndInstall.mockImplementation(() => { order.push('quitAndInstall'); });
    updater.emit('update-downloaded', { version: '1.2.0' });
    await Promise.all([splashSvc.install(), splashSvc.install()]); // double click
    expect(order).toEqual(['splash 1.2.0', 'quitAndInstall']);
  });

  it('still installs when the splash fails', async () => {
    const failing = new UpdateService(updater, () => undefined, { beforeInstall: async () => { throw new Error('no powershell'); } });
    updater.emit('update-downloaded', { version: '1.2.0' });
    await failing.install();
    expect(updater.quitAndInstall).toHaveBeenCalledWith(true, true);
  });

  it('does not start a new check over a finished download', async () => {
    updater.emit('update-downloaded', { version: '1.2.0' });
    await svc.check();
    expect(updater.checkForUpdates).not.toHaveBeenCalled();
  });

  it('reports up-to-date and errors', async () => {
    updater.emit('update-not-available', { version: '1.0.0' });
    expect(svc.get().state).toBe('up-to-date');
    updater.checkForUpdates.mockRejectedValueOnce(new Error('getaddrinfo ENOTFOUND github.com'));
    await svc.check();
    expect(svc.get()).toEqual({ state: 'error', message: 'Could not reach the update server. Check your internet connection.' });
  });

  it('checks in the background shortly after start', async () => {
    vi.useFakeTimers();
    svc.start();
    expect(updater.checkForUpdates).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(CHECK_DELAY_MS);
    expect(updater.checkForUpdates).toHaveBeenCalledOnce();
  });

  it('is unsupported (and inert) without an updater, e.g. in a dev run', async () => {
    const dev = new UpdateService(null, (s) => sent.push(s));
    expect(dev.get().state).toBe('unsupported');
    expect((await dev.check()).state).toBe('unsupported');
    dev.start();
    dev.stop();
  });
});

describe('notesToText', () => {
  it('turns release-note HTML into plain text', () => {
    expect(notesToText('<h2>New</h2><ul><li>A &amp; B</li><li>C</li></ul>')).toBe('New\n• A & B\n• C');
    expect(notesToText([{ note: 'one' }, { note: null }])).toBe('one');
    expect(notesToText(null)).toBeNull();
    expect(notesToText('<p></p>')).toBeNull();
  });

  it('never lets markup through', () => {
    expect(notesToText('<img src=x onerror=alert(1)>hi<script>bad()</script>')).not.toMatch(/[<>]/);
  });
});

describe('friendlyError', () => {
  it('keeps messages short and readable', () => {
    expect(friendlyError(new Error('Cannot find latest.yml in the latest release artifacts (404)'))).toBe('No published version was found yet.');
    expect(friendlyError(new Error('No published versions on GitHub'))).toBe('No published version was found yet.');
    expect(friendlyError(new Error('boom\nHTTP dump…'))).toBe('boom');
    expect(friendlyError(new Error('x'.repeat(300)))).toHaveLength(198);
  });
});

describe('fileLogger', () => {
  it('appends timestamped lines and rolls over when the file is too big', async () => {
    const { mkdtempSync, readFileSync, rmSync, existsSync } = await import('node:fs');
    const { tmpdir } = await import('node:os');
    const path = await import('node:path');
    const { fileLogger } = await import('./updater');
    const dir = mkdtempSync(path.join(tmpdir(), 'erp-log-'));
    const file = path.join(dir, 'logs', 'updater.log');
    const log = fileLogger(file, 200);
    log.info('Install: isSilent: true, isForceRunAfter: true');
    log.error(new Error('boom'));
    const text = readFileSync(file, 'utf8');
    expect(text).toMatch(/^\d{4}-\d\d-\d\dT.+ \[info\] Install: isSilent: true/m);
    expect(text).toMatch(/\[error\] Error: boom/);
    log.info('x'.repeat(300));
    log.info('after rollover');
    expect(existsSync(path.join(dir, 'logs', 'updater.old.log'))).toBe(true);
    expect(readFileSync(file, 'utf8')).toMatch(/after rollover/);
    rmSync(dir, { recursive: true, force: true });
  });
});
