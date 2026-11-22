import { ipcMain, type IpcMainInvokeEvent } from 'electron';
import { IPC } from '../shared/contract';
import type { ApiClient } from './api-client';
import type { ChatStream } from './chat-stream';
import type { SettingsStore } from './settings';
import type { UpdateService } from './updater';

/**
 * Registers the renderer-facing handlers. Every handler first checks the call comes from the app's own
 * page (not an injected frame or a page that was somehow navigated elsewhere).
 */
export function registerIpc(
  api: ApiClient, settings: SettingsStore, updates: UpdateService, chat: ChatStream, isApp: (url: string) => boolean,
): void {
  const guard = <A extends unknown[], R>(fn: (...args: A) => Promise<R> | R) =>
    async (event: IpcMainInvokeEvent, ...args: A): Promise<R> => {
      const url = event.senderFrame?.url ?? '';
      if (!isApp(url) || event.senderFrame?.parent) throw new Error('Blocked IPC from an untrusted frame');
      return fn(...args);
    };

  ipcMain.handle(IPC.apiRequest, guard((method: unknown, path: unknown, body?: unknown) => api.request(method, path, body)));
  ipcMain.handle(IPC.authLogin, guard((email: string, password: string, code?: string, rememberDevice?: boolean) =>
    api.login(email, password, code, rememberDevice === true)));
  ipcMain.handle(IPC.authRestore, guard(() => api.restore()));
  ipcMain.handle(IPC.authLogout, guard(() => {
    chat.stop();
    return api.logout();
  }));
  ipcMain.handle(IPC.settingsGet, guard(() => settings.get()));
  ipcMain.handle(IPC.settingsSetServerUrl, guard(async (url: string) => {
    if (await settings.setServerUrl(url)) {
      chat.stop();
      await api.reset(); // a session belongs to one server
    }
    return settings.get();
  }));
  ipcMain.handle(IPC.updatesGetStatus, guard(() => updates.get()));
  ipcMain.handle(IPC.updatesCheck, guard(() => updates.check()));
  ipcMain.handle(IPC.updatesDownload, guard(() => updates.download()));
  ipcMain.handle(IPC.updatesInstall, guard(() => updates.install()));
  ipcMain.handle(IPC.chatStart, guard(() => chat.start()));
  ipcMain.handle(IPC.chatStop, guard(() => chat.stop()));
}
