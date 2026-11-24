import { session, shell, type BrowserWindow, type WebContents } from 'electron';

/** True if `url` is the app's own UI (packaged file:// bundle, or the Vite dev server in development). */
export function isAppUrl(url: string, devServerUrl: string | undefined, rendererDir: string): boolean {
  try {
    const u = new URL(url);
    if (devServerUrl) return u.origin === new URL(devServerUrl).origin;
    if (u.protocol !== 'file:') return false;
    const file = decodeURIComponent(u.pathname).replace(/\\/g, '/');
    const dir = rendererDir.replace(/\\/g, '/').replace(/\/+$/, '');
    // Compare whole path segments: ".../dist" must not also accept a sibling folder like ".../dist-evil/".
    return file.startsWith(`${dir.startsWith('/') ? dir : `/${dir}`}/`);
  } catch {
    return false;
  }
}

/**
 * Chromium switches that would open the installed app to inspection or turn off its protections:
 * remote debugging (drive the window and its window.erp bridge from outside), certificate and web-security
 * bypasses (intercept or rewrite traffic), and the sandbox kill switch. Electron fuses already block
 * NODE_OPTIONS, --inspect and ELECTRON_RUN_AS_NODE; these are Chromium flags the fuses don't cover.
 */
const FORBIDDEN_SWITCHES = [
  'remote-debugging-port',
  'remote-debugging-pipe',
  'remote-debugging-address',
  'remote-allow-origins',
  'inspect',
  'inspect-brk',
  'inspect-port',
  'inspect-publish-uid',
  'js-flags',
  'ignore-certificate-errors',
  'ignore-certificate-errors-spki-list',
  'ignore-urlfetcher-cert-requests',
  'disable-web-security',
  'allow-insecure-localhost',
  'no-sandbox',
  'disable-gpu-sandbox',
  'unsafely-treat-insecure-origin-as-secure',
];

/** The first forbidden switch in `argv` (e.g. "--remote-debugging-port=9222" -> "remote-debugging-port"), or null. */
export function forbiddenSwitch(argv: readonly string[]): string | null {
  for (const arg of argv) {
    const m = /^--?([a-z0-9-]+)(?:=|$)/i.exec(arg);
    if (m && FORBIDDEN_SWITCHES.includes(m[1].toLowerCase())) return m[1].toLowerCase();
  }
  return null;
}

/** Process-wide hardening that does not depend on a window. Call once, after app 'ready'. */
export function hardenSession(): void {
  // The UI needs no camera, mic, geolocation, notifications, etc.
  session.defaultSession.setPermissionRequestHandler((_wc, _permission, callback) => callback(false));
  session.defaultSession.setPermissionCheckHandler(() => false);
}

/**
 * Per-window hardening: the window can only ever show the app. Links to https sites open in the
 * user's browser; every other navigation, popup or <webview> is refused.
 */
export function hardenWindow(win: BrowserWindow, isApp: (url: string) => boolean): void {
  const wc: WebContents = win.webContents;
  wc.setWindowOpenHandler(({ url }) => {
    if (url.startsWith('https://')) void shell.openExternal(url);
    return { action: 'deny' };
  });
  wc.on('will-navigate', (e, url) => {
    if (!isApp(url)) e.preventDefault();
  });
  wc.on('will-redirect', (e, url) => {
    if (!isApp(url)) e.preventDefault();
  });
  wc.on('will-attach-webview', (e) => e.preventDefault());
}
