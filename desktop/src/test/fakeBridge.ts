import type { BridgeResponse, ChatStreamEvent, DesktopSettings, ErpBridge, SessionInfo, UpdateStatus } from '../../electron/shared/contract';
import type { UserDto } from '../api/client';

/**
 * Test double for window.erp (the preload bridge). API calls are forwarded to globalThis.fetch so screen
 * tests keep using mockApi()/jsonResponse(); auth calls go to /api/auth/* the same way.
 */
export const fakeState: {
  user: UserDto | null; settings: DesktopSettings; signedOut: Set<() => void>;
  update: UpdateStatus; updateListeners: Set<(s: UpdateStatus) => void>; updateCalls: string[];
  chatRunning: boolean; chatListeners: Set<(e: ChatStreamEvent) => void>;
} = {
  user: null,
  settings: { environment: 'local', serverUrl: 'http://localhost:8088', serverUrlLocked: false },
  signedOut: new Set(),
  update: { state: 'idle' },
  updateListeners: new Set(),
  updateCalls: [],
  chatRunning: false,
  chatListeners: new Set(),
};

/** Simulates the main process pushing a new update status. */
export function emitUpdate(status: UpdateStatus) {
  fakeState.update = status;
  fakeState.updateListeners.forEach((fn) => fn(status));
}

async function viaFetch<T>(method: string, path: string, body?: unknown): Promise<BridgeResponse<T>> {
  const res = await fetch(path, { method, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = res.status === 204 ? '' : await res.text();
  return { status: res.status, body: text ? JSON.parse(text) : null };
}

export const fakeBridge: ErpBridge = {
  api: { request: (method, path, body) => viaFetch(method, path, body) },
  auth: {
    login: async (email, password, code, rememberDevice) => {
      const res = await viaFetch<{ user: UserDto; twoFactorRequired?: boolean }>('POST', '/api/auth/login',
        { email, password, code, rememberDevice: rememberDevice === true });
      if (res.status !== 200 || !res.body) return res as BridgeResponse<SessionInfo>;
      if (res.body.twoFactorRequired) return { status: 200, body: { twoFactorRequired: true } }; // like the main process
      fakeState.user = res.body.user;
      return { status: 200, body: { user: res.body.user, sessionExpiresAt: null, persistent: true } };
    },
    restore: async () => (fakeState.user ? { user: fakeState.user, sessionExpiresAt: null, persistent: true } : null),
    logout: async () => {
      fakeState.user = null;
      await viaFetch('POST', '/api/auth/logout').catch(() => undefined);
    },
    onSignedOut: (fn) => {
      fakeState.signedOut.add(fn);
      return () => fakeState.signedOut.delete(fn);
    },
  },
  settings: {
    get: async () => fakeState.settings,
    setServerUrl: async (url) => {
      if (!/^https:\/\//.test(url) && !/^http:\/\/localhost/.test(url)) throw new Error('Use https:// for remote servers');
      fakeState.settings = { ...fakeState.settings, serverUrl: url };
      return fakeState.settings;
    },
  },
  updates: {
    getStatus: async () => fakeState.update,
    check: async () => { fakeState.updateCalls.push('check'); return fakeState.update; },
    download: async () => {
      fakeState.updateCalls.push('download');
      if (fakeState.update.state === 'available') fakeState.update = { state: 'downloading', version: fakeState.update.version, percent: 0 };
      return fakeState.update;
    },
    install: async () => { fakeState.updateCalls.push('install'); },
    onStatus: (fn) => {
      fakeState.updateListeners.add(fn);
      return () => fakeState.updateListeners.delete(fn);
    },
  },
  chat: {
    start: async () => { fakeState.chatRunning = true; },
    stop: async () => { fakeState.chatRunning = false; },
    onEvent: (fn) => {
      fakeState.chatListeners.add(fn);
      return () => fakeState.chatListeners.delete(fn);
    },
  },
  app: { platform: 'test', versions: { app: '0.0.0', electron: '', chrome: '' } },
};

/** Simulates the main process forwarding a Messages live event. */
export function emitChat(event: ChatStreamEvent) {
  fakeState.chatListeners.forEach((fn) => fn(event));
}

export function resetFakeBridge() {
  fakeState.user = null;
  fakeState.settings = { environment: 'local', serverUrl: 'http://localhost:8088', serverUrlLocked: false };
  fakeState.signedOut.clear();
  fakeState.update = { state: 'idle' };
  fakeState.updateListeners.clear();
  fakeState.updateCalls = [];
  fakeState.chatRunning = false;
  fakeState.chatListeners.clear();
}
