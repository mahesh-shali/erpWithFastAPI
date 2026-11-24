/**
 * The contract between the renderer (React UI) and the main process.
 *
 * Security model: the renderer never sees a token and has no network access (CSP connect-src 'none').
 * Every API call goes through the main process, which holds the access token in memory and the
 * refresh token in OS-encrypted storage, and only lets through relative /api/* and /ai/* paths.
 */

export const IPC = {
  apiRequest: 'erp:api:request',
  authLogin: 'erp:auth:login',
  authRestore: 'erp:auth:restore',
  authLogout: 'erp:auth:logout',
  authSignedOut: 'erp:auth:signed-out',
  settingsGet: 'erp:settings:get',
  settingsSetServerUrl: 'erp:settings:set-server-url',
  updatesGetStatus: 'erp:updates:get-status',
  updatesCheck: 'erp:updates:check',
  updatesDownload: 'erp:updates:download',
  updatesInstall: 'erp:updates:install',
  updatesStatus: 'erp:updates:status',
  chatStart: 'erp:chat:start',
  chatStop: 'erp:chat:stop',
  chatEvent: 'erp:chat:event',
} as const;

/**
 * Live notice for Messages (end-to-end encrypted chat). The main process holds the event stream
 * (GET /api/chat/stream) and forwards only these validated notices: never content, never tokens.
 * ready = stream connected; reconnected = connected again after a drop (re-sync what may have been missed).
 */
export interface ChatStreamEvent {
  type: 'message' | 'conversation' | 'ready' | 'reconnected';
  conversationId?: string | null;
  messageId?: number | null;
}

/**
 * App updates from GitHub Releases (Squirrel on Windows, electron-updater on macOS/Linux). The user starts the
 * download; once downloaded, the update installs on "Restart now" or the next time the app quits.
 */
export type UpdateStatus =
  | { state: 'unsupported'; reason: string }            // dev run, or not an installed build
  | { state: 'idle' }
  | { state: 'checking' }
  | { state: 'up-to-date'; checkedAt: string }
  | { state: 'available'; version: string; releaseNotes: string | null }
  | { state: 'downloading'; version: string; percent: number | null }   // null: progress unknown (Squirrel)
  | { state: 'downloaded'; version: string }
  | { state: 'error'; message: string };

export type HttpMethod = 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE';
export const HTTP_METHODS: readonly HttpMethod[] = ['GET', 'POST', 'PUT', 'PATCH', 'DELETE'];

/** status 0 = the server could not be reached (network error, timeout, bad server URL). */
export interface BridgeResponse<T = unknown> {
  status: number;
  body: T | null;
}

export interface SessionUser {
  id: string;
  email: string;
  fullName: string;
  role: 'SuperAdmin' | 'Admin' | 'Manager' | 'User';
  organizationId: string;
}

export interface SessionInfo {
  user: SessionUser;
  sessionExpiresAt: string | null;
  /** False when the OS has no secure storage: the session then ends when the app closes. */
  persistent: boolean;
}

/** Login answer when the password was right but the account has two-factor sign-in: call login again with the code. */
export interface TwoFactorChallenge {
  twoFactorRequired: true;
}

/** local = Docker stack on this machine; production = the deployed ERP (set with ERP_ENV). */
export type ErpEnvironment = 'local' | 'production';

export interface DesktopSettings {
  environment: ErpEnvironment;
  serverUrl: string;
  /** True when ERP_SERVER_URL is set: the URL can't be changed from the UI. */
  serverUrlLocked: boolean;
}

export interface ErpBridge {
  api: {
    request<T = unknown>(method: HttpMethod, path: string, body?: unknown): Promise<BridgeResponse<T>>;
  };
  auth: {
    /**
     * `code` = authenticator or recovery code, sent after a TwoFactorChallenge. `rememberDevice` = this app skips the
     * code for 30 days (the main process keeps that token OS-encrypted; it survives sign-out).
     */
    login(email: string, password: string, code?: string, rememberDevice?: boolean)
      : Promise<BridgeResponse<SessionInfo | TwoFactorChallenge>>;
    /** Resumes the last session with the stored refresh token; null if there is none or it was revoked. */
    restore(): Promise<SessionInfo | null>;
    logout(): Promise<void>;
    /** Fires when the main process loses the session (refresh rejected, revoked elsewhere). */
    onSignedOut(listener: () => void): () => void;
  };
  settings: {
    get(): Promise<DesktopSettings>;
    /** Changing the server signs the user out. Throws on an invalid URL. */
    setServerUrl(url: string): Promise<DesktopSettings>;
  };
  updates: {
    getStatus(): Promise<UpdateStatus>;
    /** Looks for a newer release. */
    check(): Promise<UpdateStatus>;
    /** Downloads the available update (progress arrives through onStatus). */
    download(): Promise<UpdateStatus>;
    /** Quits, installs the downloaded update and starts the new version. */
    install(): Promise<void>;
    onStatus(listener: (status: UpdateStatus) => void): () => void;
  };
  chat: {
    /** Opens the live event stream (idempotent). */
    start(): Promise<void>;
    stop(): Promise<void>;
    onEvent(listener: (event: ChatStreamEvent) => void): () => void;
  };
  app: { platform: string; versions: { app: string; electron: string; chrome: string } };
}
