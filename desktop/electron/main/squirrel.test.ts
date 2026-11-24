import { EventEmitter } from 'node:events';
import path from 'node:path';
import { describe, expect, it, vi } from 'vitest';
import {
  compareVersions, handleSquirrelEvent, isSquirrelInstall, latestVersion, squirrelEvent, SquirrelUpdater,
  updateExePath, type NativeAutoUpdater,
} from './squirrel';
import { UpdateService } from './updater';

const ROOT = path.resolve('/Users/u/AppData/Local/erp-desktop');
const EXE = path.join(ROOT, 'app-1.4.0', 'ERP Platform.exe');
const UPDATE_EXE = path.join(ROOT, 'Update.exe');

const RELEASES = [
  'A1B2C3 erp-desktop-1.4.0-full.nupkg 104857600',
  'D4E5F6 erp-desktop-1.5.0-delta.nupkg 2048',
  'D4E5F7 erp-desktop-1.5.0-full.nupkg 104857700',
].join('\r\n');

class FakeNative extends EventEmitter implements NativeAutoUpdater {
  setFeedURL = vi.fn();
  checkForUpdates = vi.fn();
  quitAndInstall = vi.fn();
}

function makeUpdater(releases = RELEASES, current = '1.4.0') {
  const native = new FakeNative();
  const fetchText = vi.fn(async (url: string) => {
    if (url.endsWith('/RELEASES')) return releases;
    if (url.includes('api.github.com')) return JSON.stringify({ body: 'Faster orders' });
    throw new Error(`404 for ${url}`);
  });
  const updater = new SquirrelUpdater(native, {
    feedUrl: 'https://github.com/o/r/releases/latest/download/',
    releaseApiUrl: 'https://api.github.com/repos/o/r/releases/latest',
    currentVersion: current,
    fetchText,
  });
  return { native, fetchText, updater };
}

describe('Squirrel install layout', () => {
  it('finds Update.exe one folder above app-<version>', () => {
    expect(updateExePath(EXE)).toBe(UPDATE_EXE);
    expect(isSquirrelInstall(EXE, (p) => p === UPDATE_EXE)).toBe(true);
    expect(isSquirrelInstall(EXE, () => false)).toBe(false);
  });
});

describe('handleSquirrelEvent', () => {
  const fakeRun = () => {
    const child = new EventEmitter();
    const run = vi.fn((_exe: string, _args: string[]) => child);
    return { child, run };
  };

  it('ignores normal starts and the first run after install', () => {
    const quit = vi.fn();
    expect(squirrelEvent([EXE])).toBeNull();
    expect(handleSquirrelEvent([EXE], EXE, quit)).toBe(false);
    expect(handleSquirrelEvent([EXE, '--squirrel-firstrun'], EXE, quit)).toBe(false);
    expect(quit).not.toHaveBeenCalled();
  });

  it.each([
    ['--squirrel-install', '--createShortcut=ERP Platform.exe'],
    ['--squirrel-updated', '--createShortcut=ERP Platform.exe'],
    ['--squirrel-uninstall', '--removeShortcut=ERP Platform.exe'],
  ])('%s runs Update.exe %s, then quits', (flag, arg) => {
    const quit = vi.fn();
    const { child, run } = fakeRun();
    expect(handleSquirrelEvent([EXE, flag, '1.4.0'], EXE, quit, run)).toBe(true);
    expect(run).toHaveBeenCalledWith(UPDATE_EXE, [arg]);
    expect(quit).not.toHaveBeenCalled();
    child.emit('close');
    child.emit('close');
    expect(quit).toHaveBeenCalledOnce();
  });

  it('quits right away for an obsolete version and when Update.exe cannot start', () => {
    const quit = vi.fn();
    expect(handleSquirrelEvent([EXE, '--squirrel-obsolete'], EXE, quit)).toBe(true);
    expect(quit).toHaveBeenCalledOnce();
    const quit2 = vi.fn();
    handleSquirrelEvent([EXE, '--squirrel-install'], EXE, quit2, () => { throw new Error('ENOENT'); });
    expect(quit2).toHaveBeenCalledOnce();
  });

  it('never waits longer than Squirrel allows', () => {
    vi.useFakeTimers();
    try {
      const quit = vi.fn();
      handleSquirrelEvent([EXE, '--squirrel-install'], EXE, quit, fakeRun().run);
      vi.advanceTimersByTime(10_000);
      expect(quit).toHaveBeenCalledOnce();
    } finally {
      vi.useRealTimers();
    }
  });
});

describe('RELEASES parsing', () => {
  it('picks the highest version from full and delta packages', () => {
    expect(latestVersion(RELEASES)).toBe('1.5.0');
    expect(latestVersion('﻿16190D8B erp-desktop-1.3.1-full.nupkg 155201813')).toBe('1.3.1'); // BOM, as built
    expect(latestVersion('')).toBeNull();
    expect(latestVersion('garbage line\n\n')).toBeNull();
  });

  it('compares versions numerically, pre-releases first', () => {
    expect(compareVersions('1.10.0', '1.9.9')).toBe(1);
    expect(compareVersions('1.4.0', '1.4.0')).toBe(0);
    expect(compareVersions('1.4.0-beta1', '1.4.0')).toBe(-1);
    expect(compareVersions('1.3.1', '1.4.0')).toBe(-1);
  });
});

describe('SquirrelUpdater', () => {
  it('points Squirrel at the feed and never throws on a stray native error', () => {
    const { native } = makeUpdater();
    expect(native.setFeedURL).toHaveBeenCalledWith({ url: 'https://github.com/o/r/releases/latest/download/' });
    expect(() => native.emit('error', new Error('offline'))).not.toThrow();
  });

  it('checks by reading RELEASES only (no download) and reports the newer version with notes', async () => {
    const { native, fetchText, updater } = makeUpdater();
    const events: unknown[] = [];
    updater.on('checking-for-update', () => events.push('checking'));
    updater.on('update-available', (info) => events.push(info));
    await updater.checkForUpdates();
    expect(fetchText).toHaveBeenCalledWith('https://github.com/o/r/releases/latest/download/RELEASES');
    expect(events).toEqual(['checking', { version: '1.5.0', releaseNotes: 'Faster orders' }]);
    expect(native.checkForUpdates).not.toHaveBeenCalled();
  });

  it('reports up to date when nothing newer is published', async () => {
    const { updater } = makeUpdater('X erp-desktop-1.4.0-full.nupkg 1', '1.4.0');
    const none = vi.fn();
    updater.on('update-not-available', none);
    await updater.checkForUpdates();
    expect(none).toHaveBeenCalledOnce();
  });

  it('downloads through Squirrel on request and restarts into the new version', async () => {
    const { native, updater } = makeUpdater();
    await updater.checkForUpdates();
    const downloaded = vi.fn();
    updater.on('update-downloaded', downloaded);
    const p = updater.downloadUpdate();
    expect(updater.downloadUpdate()).toBe(p); // a second click joins the running download
    expect(native.checkForUpdates).toHaveBeenCalledOnce();
    native.emit('update-downloaded', {}, '', '1.5.0');
    await expect(p).resolves.toEqual({ version: '1.5.0' });
    expect(downloaded).toHaveBeenCalledWith({ version: '1.5.0' });
    updater.quitAndInstall();
    expect(native.quitAndInstall).toHaveBeenCalledOnce();
  });

  it('fails the download when Squirrel errors', async () => {
    const { native, updater } = makeUpdater();
    await updater.checkForUpdates();
    const p = updater.downloadUpdate();
    native.emit('error', new Error('hash mismatch'));
    await expect(p).rejects.toThrow('hash mismatch');
  });

  it('drives UpdateService end to end: check → download → downloaded → install', async () => {
    const { native, updater } = makeUpdater();
    const svc = new UpdateService(updater, () => undefined);
    await svc.check();
    expect(svc.get()).toEqual({ state: 'available', version: '1.5.0', releaseNotes: 'Faster orders' });
    const dl = svc.download();
    expect(svc.get()).toEqual({ state: 'downloading', version: '1.5.0', percent: null });
    native.emit('update-downloaded');
    await dl;
    expect(svc.get()).toEqual({ state: 'downloaded', version: '1.5.0' });
    await svc.install();
    expect(native.quitAndInstall).toHaveBeenCalledOnce();
    svc.stop();
  });

  it('shows "no published version" when RELEASES is missing', async () => {
    const native = new FakeNative();
    const updater = new SquirrelUpdater(native, {
      feedUrl: 'https://x/download', currentVersion: '1.4.0',
      fetchText: async () => { throw new Error('404 Not Found for https://x/download/RELEASES'); },
    });
    const svc = new UpdateService(updater, () => undefined);
    await svc.check();
    expect(svc.get()).toEqual({ state: 'error', message: 'No published version was found yet.' });
  });
});
