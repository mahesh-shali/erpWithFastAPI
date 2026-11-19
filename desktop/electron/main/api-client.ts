import { HTTP_METHODS, type BridgeResponse, type HttpMethod, type SessionInfo, type SessionUser } from '../shared/contract';

/** Where the refresh token lives between app runs (see token-vault.ts). */
export interface RefreshTokenStore {
  readonly persistent: boolean;
  load(): Promise<string | null>;
  save(token: string): Promise<void>;
  clear(): Promise<void>;
}

export interface ApiClientOptions {
  /** Base URL of the ERP edge (nginx), e.g. https://erp.example.com. Read on every call. */
  baseUrl: () => string;
  store: RefreshTokenStore;
  fetch?: typeof fetch;
  /** Name of the API's HttpOnly refresh cookie (Jwt:CookieName in the API). */
  cookieName?: string;
  userAgent?: string;
  timeoutMs?: number;
  /** Called when a session is lost without the user asking (refresh rejected / revoked). */
  onSignedOut?: () => void;
}

interface AuthResponseBody {
  accessToken: string;
  expiresAt: string;
  user: SessionUser;
  sessionId?: string;
  sessionExpiresAt?: string;
}

const MAX_BODY_BYTES = 1_000_000;
/** Relative API paths only: no scheme, host, "..", backslashes or control characters. */
const PATH_RE = /^\/(api|ai)\/[A-Za-z0-9\-._~/?=&%:,+]*$/;

export function isAllowedPath(path: unknown): path is string {
  return typeof path === 'string' && path.length <= 2048 && PATH_RE.test(path) && !path.includes('..') && !path.includes('//');
}

export function isAllowedMethod(method: unknown): method is HttpMethod {
  return typeof method === 'string' && (HTTP_METHODS as readonly string[]).includes(method);
}

/** Reads `name=value` out of Set-Cookie headers. Returns '' when the server deletes the cookie. */
export function readCookie(setCookies: string[], name: string): string | null {
  for (const header of setCookies) {
    const [pair, ...attrs] = header.split(';');
    const eq = pair.indexOf('=');
    if (eq < 0 || pair.slice(0, eq).trim() !== name) continue;
    const value = pair.slice(eq + 1).trim();
    const expired = attrs.some((a) => {
      const [k, v] = a.split('=').map((s) => s.trim().toLowerCase());
      return (k === 'max-age' && Number(v) <= 0) || (k === 'expires' && Date.parse(v) <= Date.now());
    });
    return expired ? '' : value;
  }
  return null;
}

/**
 * HTTP client that runs in the Electron main process and owns the session.
 *
 * - Access token (15 min JWT): memory only, never sent to the renderer.
 * - Refresh token: the API sets it as an HttpOnly cookie; we read it from Set-Cookie, keep it in the
 *   RefreshTokenStore (OS-encrypted) and send it back as a Cookie header on /api/auth/refresh|logout.
 *   The API rotates it on every refresh and revokes the session if an old one is replayed.
 * - A 401 triggers one shared refresh and one retry, exactly like the web client.
 */
export class ApiClient {
  private accessToken: string | null = null;
  private refreshToken: string | null = null;
  private user: SessionUser | null = null;
  private sessionExpiresAt: string | null = null;
  private refreshing: Promise<boolean> | null = null;
  private loaded = false;
  private readonly fetchImpl: typeof fetch;
  private readonly cookieName: string;

  constructor(private readonly opts: ApiClientOptions) {
    this.fetchImpl = opts.fetch ?? fetch;
    this.cookieName = opts.cookieName ?? 'erp_refresh';
  }

  get currentUser(): SessionUser | null {
    return this.user;
  }

  async login(email: string, password: string): Promise<BridgeResponse<SessionInfo>> {
    if (typeof email !== 'string' || typeof password !== 'string' || email.length > 320 || password.length > 1024) {
      return { status: 400, body: null };
    }
    await this.clearSession(false);
    const res = await this.send('POST', '/api/auth/login', { email, password });
    if (!res.ok) return { status: res.status, body: res.body as SessionInfo | null };
    await this.acceptAuth(res.body as AuthResponseBody, res.setCookies);
    return { status: res.status, body: this.sessionInfo() };
  }

  /** Startup: resume the previous session from the stored refresh token. */
  async restore(): Promise<SessionInfo | null> {
    if (this.user && this.accessToken) return this.sessionInfo();
    await this.loadStoredToken();
    if (!this.refreshToken) return null;
    return (await this.refresh()) ? this.sessionInfo() : null;
  }

  async logout(): Promise<void> {
    await this.loadStoredToken();
    if (this.accessToken || this.refreshToken) {
      // Best effort: revoke on the server; the local session is dropped whatever happens.
      await this.send('POST', '/api/auth/logout', undefined, true).catch(() => undefined);
    }
    await this.clearSession(false);
  }

  /** Forgets the session locally (e.g. after the server URL changed). */
  async reset(): Promise<void> {
    await this.clearSession(false);
  }

  async request(method: unknown, path: unknown, body?: unknown): Promise<BridgeResponse> {
    if (!isAllowedMethod(method) || !isAllowedPath(path)) return { status: 400, body: { title: 'Blocked request' } };
    if (path.startsWith('/api/auth/login') || path.startsWith('/api/auth/refresh') || path.startsWith('/api/auth/logout')) {
      return { status: 400, body: { title: 'Use the auth bridge for this endpoint' } };
    }
    if (body !== undefined && JSON.stringify(body).length > MAX_BODY_BYTES) return { status: 413, body: { title: 'Request too large' } };

    const token = this.accessToken;
    let res = await this.send(method, path, body);
    if (res.status === 401) {
      // A concurrent call may have refreshed already; otherwise refresh once and retry.
      const refreshed = this.accessToken !== token || (this.refreshToken !== null && (await this.refresh()));
      if (refreshed) res = await this.send(method, path, body);
      if (res.status === 401) await this.clearSession(true);
    }
    return { status: res.status, body: res.body };
  }

  // ---------- internals ----------

  private refresh(): Promise<boolean> {
    this.refreshing ??= (async () => {
      try {
        const res = await this.send('POST', '/api/auth/refresh', undefined, true);
        if (!res.ok) {
          // 401 = refresh token rejected (expired, revoked, reused). Network errors keep it for a retry.
          if (res.status === 401 || res.status === 403) await this.clearSession(true);
          return false;
        }
        await this.acceptAuth(res.body as AuthResponseBody, res.setCookies);
        return true;
      } finally {
        this.refreshing = null;
      }
    })();
    return this.refreshing;
  }

  private async acceptAuth(body: AuthResponseBody, setCookies: string[]) {
    if (!body?.accessToken || !body.user) throw new Error('Malformed auth response');
    this.accessToken = body.accessToken;
    this.user = body.user;
    this.sessionExpiresAt = body.sessionExpiresAt ?? null;
    const cookie = readCookie(setCookies, this.cookieName);
    if (cookie) {
      this.refreshToken = cookie;
      await this.opts.store.save(cookie);
    }
  }

  private sessionInfo(): SessionInfo | null {
    return this.user
      ? { user: this.user, sessionExpiresAt: this.sessionExpiresAt, persistent: this.opts.store.persistent }
      : null;
  }

  private async loadStoredToken() {
    if (this.loaded) return;
    this.loaded = true;
    this.refreshToken ??= await this.opts.store.load().catch(() => null);
  }

  private async clearSession(notify: boolean) {
    const hadUser = this.user !== null;
    this.accessToken = null;
    this.refreshToken = null;
    this.user = null;
    this.sessionExpiresAt = null;
    this.loaded = true;
    await this.opts.store.clear().catch(() => undefined);
    if (notify && hadUser) this.opts.onSignedOut?.();
  }

  private async send(method: HttpMethod, path: string, body: unknown, withRefreshCookie = false) {
    const headers: Record<string, string> = { Accept: 'application/json' };
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    if (this.accessToken) headers.Authorization = `Bearer ${this.accessToken}`;
    if (withRefreshCookie && this.refreshToken) headers.Cookie = `${this.cookieName}=${this.refreshToken}`;
    if (this.opts.userAgent) headers['User-Agent'] = this.opts.userAgent;

    let url: string;
    try {
      url = new URL(path, this.opts.baseUrl()).toString();
    } catch {
      return { ok: false, status: 0, body: { title: 'Invalid server URL' } as unknown, setCookies: [] as string[] };
    }
    try {
      const res = await this.fetchImpl(url, {
        method,
        headers,
        body: body === undefined ? undefined : JSON.stringify(body),
        redirect: 'error',
        signal: AbortSignal.timeout(this.opts.timeoutMs ?? 60_000),
      });
      const text = res.status === 204 ? '' : await res.text();
      let parsed: unknown = null;
      if (text) {
        try { parsed = JSON.parse(text); } catch { parsed = { title: res.statusText || 'Unexpected response' }; }
      }
      const setCookies = typeof res.headers.getSetCookie === 'function' ? res.headers.getSetCookie() : [];
      return { ok: res.ok, status: res.status, body: parsed, setCookies };
    } catch (e) {
      const timeout = e instanceof Error && (e.name === 'TimeoutError' || e.name === 'AbortError');
      return {
        ok: false, status: 0, setCookies: [] as string[],
        body: { title: timeout ? 'The server took too long to respond' : 'Cannot reach the ERP server' } as unknown,
      };
    }
  }
}
