import { app, BrowserWindow, Menu, screen, shell } from 'electron';
import path from 'node:path';
import { IPC } from '../shared/contract';
import { ApiClient } from './api-client';
import { resolveEnvironment } from './environment';
import { ChatStream } from './chat-stream';
import { registerIpc } from './ipc';
import { forbiddenSwitch, hardenSession, hardenWindow, isAppUrl } from './security';
import { SettingsStore } from './settings';
import { TokenVault } from './token-vault';
import { showUpdateSplash } from './update-splash';
import { fileLogger, UpdateService } from './updater';
import { usableBounds, WindowStateStore } from './window-state';

// The installed app never runs with debugging or security-bypass switches (e.g. --remote-debugging-port would let
// another program drive the window and its window.erp bridge). Exit before anything starts. Dev runs and the
// Playwright tests launch unpackaged and are unaffected.
if (app.isPackaged) {
  const bad = forbiddenSwitch(process.argv);
  if (bad) {
    console.error(`ERP Platform: refusing to start with --${bad}`);
    process.exit(1);
  }
}

// Bundled by scripts/build-electron.mjs into dist-electron/main.cjs; the UI is in dist/ (Vite).
const RENDERER_DIR = path.join(__dirname, '..', 'dist');
const DEV_SERVER_URL = app.isPackaged ? undefined : process.env.ELECTRON_RENDERER_URL;
const isApp = (url: string) => isAppUrl(url, DEV_SERVER_URL, RENDERER_DIR);

let mainWindow: BrowserWindow | null = null;

// Local (Docker) or production; see environment.ts.
const ENVIRONMENT = resolveEnvironment(process.env.ERP_ENV, app.isPackaged);

// Each mode has its own profile (settings + encrypted session), so a production session is never sent to
// the local stack or the other way round, and both modes can run at once. Production keeps the default
// folder; ERP_USER_DATA_DIR overrides both (tests).
if (process.env.ERP_USER_DATA_DIR) app.setPath('userData', process.env.ERP_USER_DATA_DIR);
else if (ENVIRONMENT === 'local') app.setPath('userData', `${app.getPath('userData')} (Local)`);

// One instance per user: a second launch focuses the existing window (and avoids two refresh-token
// owners racing each other, which the API would treat as token reuse).
if (!app.requestSingleInstanceLock()) {
  app.quit();
} else {
  app.on('second-instance', () => {
    if (!mainWindow) return;
    if (mainWindow.isMinimized()) mainWindow.restore();
    mainWindow.focus();
  });
  app.whenReady().then(start).catch((e) => {
    console.error('Startup failed', e);
    app.exit(1);
  });
}

async function start() {
  hardenSession();
  const settings = new SettingsStore(ENVIRONMENT);
  await settings.load();

  let chat: ChatStream | null = null;
  const api = new ApiClient({
    baseUrl: () => settings.url,
    store: new TokenVault(),
    trustStore: new TokenVault('trusted-device.bin'), // survives sign-out: skips the 2FA code for 30 days
    userAgent: `ERP-Desktop/${app.getVersion()} (${process.platform}) Electron/${process.versions.electron}`,
    onSignedOut: () => {
      chat?.stop();
      mainWindow?.webContents.send(IPC.authSignedOut);
    },
  });
  // Messages live updates: the stream lives here; the renderer only receives validated notices.
  chat = new ChatStream(api, (event) => mainWindow?.webContents.send(IPC.chatEvent, event));
  // Updates from GitHub Releases, only in installed builds (a dev run has no release to update from).
  const updater = app.isPackaged ? (await import('electron-updater')).autoUpdater : null;
  const updateLog = fileLogger(path.join(app.getPath('userData'), 'logs', 'updater.log'));
  if (updater) {
    updater.logger = updateLog;
    updateLog.info(`App ${app.getVersion()} started (${ENVIRONMENT} mode)`);
  }
  const updates = new UpdateService(updater, (status) => mainWindow?.webContents.send(IPC.updatesStatus, status), {
    log: updateLog,
    // Keep a loading screen over the app window while the installer runs (the app itself has quit by then).
    beforeInstall: async (version) => {
      if (!mainWindow || process.platform !== 'win32') return false;
      const dip = mainWindow.getBounds();
      return showUpdateSplash({
        version,
        bounds: screen.dipToScreenRect(mainWindow, dip),
        scale: screen.getDisplayMatching(dip).scaleFactor,
      });
    },
  });
  registerIpc(api, settings, updates, chat, isApp);
  Menu.setApplicationMenu(buildMenu());

  createWindow();
  updates.start();
  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) createWindow();
  });
}

function createWindow() {
  // Reopen with the size, position and maximised state the user left the window in.
  const windowState = WindowStateStore.in(app.getPath('userData'));
  const saved = windowState.load();
  mainWindow = new BrowserWindow({
    ...usableBounds(saved?.bounds, screen.getAllDisplays().map((d) => d.workArea)),
    minWidth: 960,
    minHeight: 600,
    show: false,
    title: ENVIRONMENT === 'local' ? 'ERP Platform (Local)' : 'ERP Platform',
    backgroundColor: '#f5f7fb',
    // Hide the menu bar on Windows/Linux; Alt shows it. Shortcuts (zoom, reload, DevTools) keep working.
    autoHideMenuBar: true,
    // Installed builds take the icon from the executable; dev runs would otherwise show Electron's.
    ...(app.isPackaged ? {} : { icon: path.join(__dirname, '..', 'build', 'icon.png') }),
    webPreferences: {
      preload: path.join(__dirname, 'preload.cjs'),
      contextIsolation: true,
      sandbox: true,
      nodeIntegration: false,
      webSecurity: true,
      spellcheck: false,
      // No DevTools in the installed app: they would expose the renderer and its window.erp bridge.
      devTools: !app.isPackaged,
      additionalArguments: [`--erp-app-version=${app.getVersion()}`],
    },
  });
  hardenWindow(mainWindow, isApp);
  // Keep the window title (it names the mode) instead of the page's static <title>.
  mainWindow.on('page-title-updated', (e) => e.preventDefault());
  mainWindow.once('ready-to-show', () => {
    if (saved?.maximized) mainWindow?.maximize();
    mainWindow?.show();
  });
  mainWindow.on('close', () => {
    if (!mainWindow) return;
    windowState.save({ bounds: mainWindow.getNormalBounds(), maximized: mainWindow.isMaximized() });
  });
  mainWindow.on('closed', () => { mainWindow = null; });

  if (DEV_SERVER_URL) void mainWindow.loadURL(DEV_SERVER_URL);
  else void mainWindow.loadFile(path.join(RENDERER_DIR, 'index.html'));
}

function buildMenu(): Menu {
  const isMac = process.platform === 'darwin';
  return Menu.buildFromTemplate([
    ...(isMac ? [{ role: 'appMenu' as const }] : []),
    { role: 'fileMenu' },
    { role: 'editMenu' },
    {
      label: 'View',
      submenu: [
        { role: 'reload' },
        ...(app.isPackaged ? [] : [{ role: 'toggleDevTools' as const }]),
        { type: 'separator' },
        { role: 'resetZoom' }, { role: 'zoomIn' }, { role: 'zoomOut' },
        { type: 'separator' },
        { role: 'togglefullscreen' },
      ],
    },
    { role: 'windowMenu' },
    {
      role: 'help',
      submenu: [{
        label: 'Project on GitHub',
        click: () => void shell.openExternal('https://github.com/mahesh-shali/erpWithFastAPI'),
      }],
    },
  ]);
}

app.on('window-all-closed', () => {
  if (process.platform !== 'darwin') app.quit();
});
