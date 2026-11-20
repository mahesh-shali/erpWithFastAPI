import { contextBridge, ipcRenderer } from 'electron';
import { IPC, type ChatStreamEvent, type ErpBridge, type UpdateStatus } from '../shared/contract';

/**
 * The only API the renderer gets (window.erp). It exposes narrow functions, never ipcRenderer itself,
 * and carries no tokens: authentication happens entirely in the main process.
 */
const bridge: ErpBridge = {
  api: {
    request: (method, path, body) => ipcRenderer.invoke(IPC.apiRequest, method, path, body),
  },
  auth: {
    login: (email, password) => ipcRenderer.invoke(IPC.authLogin, email, password),
    restore: () => ipcRenderer.invoke(IPC.authRestore),
    logout: () => ipcRenderer.invoke(IPC.authLogout),
    onSignedOut: (listener) => {
      const handler = () => listener();
      ipcRenderer.on(IPC.authSignedOut, handler);
      return () => ipcRenderer.removeListener(IPC.authSignedOut, handler);
    },
  },
  settings: {
    get: () => ipcRenderer.invoke(IPC.settingsGet),
    setServerUrl: (url) => ipcRenderer.invoke(IPC.settingsSetServerUrl, url),
  },
  updates: {
    getStatus: () => ipcRenderer.invoke(IPC.updatesGetStatus),
    check: () => ipcRenderer.invoke(IPC.updatesCheck),
    download: () => ipcRenderer.invoke(IPC.updatesDownload),
    install: () => ipcRenderer.invoke(IPC.updatesInstall),
    onStatus: (listener) => {
      const handler = (_e: unknown, status: UpdateStatus) => listener(status);
      ipcRenderer.on(IPC.updatesStatus, handler);
      return () => ipcRenderer.removeListener(IPC.updatesStatus, handler);
    },
  },
  chat: {
    start: () => ipcRenderer.invoke(IPC.chatStart),
    stop: () => ipcRenderer.invoke(IPC.chatStop),
    onEvent: (listener) => {
      const handler = (_e: unknown, event: ChatStreamEvent) => listener(event);
      ipcRenderer.on(IPC.chatEvent, handler);
      return () => ipcRenderer.removeListener(IPC.chatEvent, handler);
    },
  },
  app: {
    platform: process.platform,
    versions: {
      app: process.argv.find((a) => a.startsWith('--erp-app-version='))?.split('=')[1] ?? '',
      electron: process.versions.electron ?? '',
      chrome: process.versions.chrome ?? '',
    },
  },
};

contextBridge.exposeInMainWorld('erp', bridge);
