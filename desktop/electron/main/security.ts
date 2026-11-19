import { session, shell, type BrowserWindow, type WebContents } from 'electron';

/** True if `url` is the app's own UI (packaged file:// bundle, or the Vite dev server in development). */
export function isAppUrl(url: string, devServerUrl: string | undefined, rendererDir: string): boolean {
  try {
    const u = new URL(url);
    if (devServerUrl) return u.origin === new URL(devServerUrl).origin;
    if (u.protocol !== 'file:') return false;
    const file = decodeURIComponent(u.pathname).replace(/\\/g, '/');
    const dir = rendererDir.replace(/\\/g, '/');
    return file.startsWith(dir.startsWith('/') ? dir : `/${dir}`);
  } catch {
    return false;
  }
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
