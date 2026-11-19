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

    svc.install();
    expect(updater.quitAndInstall).toHaveBeenCalledWith(false, true);
  });

  it('ignores download and install when there is nothing to download or install', async () => {
    await svc.download();
    svc.install();
    expect(updater.downloadUpdate).not.toHaveBeenCalled();
    expect(updater.quitAndInstall).not.toHaveBeenCalled();
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
