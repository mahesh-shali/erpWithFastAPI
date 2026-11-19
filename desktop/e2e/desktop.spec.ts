import { _electron as electron, expect, test, type ElectronApplication, type Page } from '@playwright/test';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import type { ErpBridge } from '../electron/shared/contract';
import { startMockServer, type MockServer } from './mock-server';

const VERSION = (JSON.parse(readFileSync(path.join(__dirname, '..', 'package.json'), 'utf8')) as { version: string }).version;

declare global {
  interface Window { erp: ErpBridge }
}

let server: MockServer;
let userData: string;

test.beforeEach(async () => {
  server = await startMockServer();
  userData = mkdtempSync(path.join(tmpdir(), 'erp-desktop-'));
});
test.afterEach(async () => {
  await server.close();
  rmSync(userData, { recursive: true, force: true });
});

async function launch(): Promise<{ app: ElectronApplication; win: Page }> {
  const app = await electron.launch({
    args: [path.join(__dirname, '..'), '--no-sandbox'],
    env: { ...process.env, ERP_SERVER_URL: server.url, ERP_USER_DATA_DIR: userData },
  });
  const win = await app.firstWindow();
  return { app, win };
}

async function signIn(win: Page) {
  await win.getByLabel('Email').fill('admin@demo.com');
  await win.getByLabel('Password').fill('Admin@123');
  await win.getByRole('button', { name: 'Sign in' }).click();
  await expect(win.getByRole('heading', { level: 1, name: 'Dashboard' })).toBeVisible();
}

test('signs in, shows the permission-driven menu and data, and signs out', async () => {
  const { app, win } = await launch();
  await expect(win.getByRole('form', { name: 'login form' })).toBeVisible();
  await expect(win.getByText(server.url)).toBeVisible(); // locked by ERP_SERVER_URL

  await win.getByLabel('Email').fill('admin@demo.com');
  await win.getByLabel('Password').fill('wrong');
  await win.getByRole('button', { name: 'Sign in' }).click();
  await expect(win.getByRole('alert')).toHaveText('Invalid email or password.');

  await signIn(win);
  await expect(win.getByText('₹98,766').first()).toBeVisible();
  await win.screenshot({ path: 'test-results/dashboard.png' });

  await win.getByRole('button', { name: /Master/ }).click();
  await win.getByRole('button', { name: /Parties/ }).click().catch(() => undefined);
  await win.getByRole('link', { name: 'Customers' }).click();
  await expect(win.getByRole('cell', { name: 'Acme Ltd' })).toBeVisible();

  await win.getByRole('link', { name: 'Devices' }).click();
  await expect(win.getByText(`Desktop app ${VERSION} on`, { exact: false })).toBeVisible();
  await win.screenshot({ path: 'test-results/devices.png' });

  // Update symbol: a dev (unpackaged) run has no release to update from and says so.
  await win.getByRole('button', { name: 'Updates' }).click();
  const updates = win.getByRole('dialog', { name: 'App updates' });
  await expect(updates).toContainText(`Installed version ${VERSION}`);
  await expect(updates).toContainText('Updates are available in the installed app.');
  await win.keyboard.press('Escape');

  await win.getByRole('button', { name: 'Sign out' }).click();
  await expect(win.getByRole('form', { name: 'login form' })).toBeVisible();
  expect(server.log).toContain('POST /api/auth/logout');
  await app.close();
});

test('the renderer has no Node, no tokens and no network access', async () => {
  const { app, win } = await launch();
  await signIn(win);
  const probe = await win.evaluate(async () => {
    const w = window as unknown as Record<string, unknown>;
    let fetchBlocked = false;
    try { await fetch(location.origin); } catch { fetchBlocked = true; }
    return {
      require: typeof w.require,
      process: typeof w.process,
      bridgeKeys: Object.keys(window.erp).sort(),
      storage: JSON.stringify({ ...localStorage }) + JSON.stringify({ ...sessionStorage }) + document.cookie,
      fetchBlocked,
    };
  });
  expect(probe.require).toBe('undefined');
  expect(probe.process).toBe('undefined');
  expect(probe.bridgeKeys).toEqual(['api', 'app', 'auth', 'settings', 'updates']);
  expect(probe.storage).not.toMatch(/[0-9a-f]{32}/); // no access or refresh token anywhere in the page
  expect(probe.fetchBlocked).toBe(true); // CSP connect-src 'none'

  // The bridge refuses anything but relative /api and /ai paths.
  const blocked = await win.evaluate(() => window.erp.api.request('GET', 'https://example.com/steal'));
  expect(blocked.status).toBe(400);
  await app.close();
});

test('a session revoked on the server sends the user back to sign-in', async () => {
  const { app, win } = await launch();
  await signIn(win);
  server.revokeAll(); // e.g. "sign out all other devices" from another machine
  await win.getByRole('link', { name: 'Analytics', exact: true }).click(); // 401 -> refresh rejected -> signed out
  await expect(win.getByRole('form', { name: 'login form' })).toBeVisible();
  await app.close();
});

test('restart resumes the session when the OS key store is available', async () => {
  let { app, win } = await launch();
  await signIn(win);
  const persistent = await app.evaluate(({ safeStorage }) =>
    safeStorage.isEncryptionAvailable() && safeStorage.getSelectedStorageBackend?.() !== 'basic_text');
  await app.close();

  ({ app, win } = await launch());
  if (persistent) {
    await expect(win.getByRole('heading', { level: 1, name: 'Dashboard' })).toBeVisible();
    expect(server.log.filter((l) => l === 'POST /api/auth/refresh').length).toBeGreaterThan(0);
  } else {
    // No keyring (e.g. headless Linux CI): nothing was written to disk, so the user signs in again.
    await expect(win.getByRole('form', { name: 'login form' })).toBeVisible();
  }
  await app.close();
});
