import { describe, expect, it, vi } from 'vitest';
import { ApiClient, isAllowedPath, readCookie, type RefreshTokenStore } from './api-client';

const user = { id: 'u1', email: 'admin@demo.com', fullName: 'Admin', role: 'Admin' as const, organizationId: 'o1' };
const BASE = 'https://erp.test';

class MemoryStore implements RefreshTokenStore {
  persistent = true;
  constructor(public token: string | null = null) {}
  load = vi.fn(async () => this.token);
  save = vi.fn(async (t: string) => { this.token = t; });
  clear = vi.fn(async () => { this.token = null; });
}

function json(body: unknown, status = 200, cookies: string[] = []) {
  const headers = new Headers({ 'Content-Type': 'application/json' });
  for (const c of cookies) headers.append('Set-Cookie', c);
  return new Response(status === 204 ? null : JSON.stringify(body), { status, headers });
}
const authBody = (token: string) => ({ accessToken: token, expiresAt: 'x', user, sessionExpiresAt: '2030-01-01T00:00:00Z' });
const refreshCookie = (v: string) => `erp_refresh=${v}; path=/api/auth; secure; samesite=strict; httponly`;

type Handler = (url: string, init: RequestInit) => Response | Promise<Response>;
function setup(handler: Handler, store = new MemoryStore()) {
  const fetchMock = vi.fn(async (url: string | URL | Request, init?: RequestInit) => handler(String(url), init ?? {}));
  const onSignedOut = vi.fn();
  const client = new ApiClient({ baseUrl: () => BASE, store, fetch: fetchMock as unknown as typeof fetch, onSignedOut });
  const header = (i: number, name: string) => (fetchMock.mock.calls[i][1]!.headers as Record<string, string>)[name];
  return { client, fetchMock, store, onSignedOut, header };
}

describe('path and cookie helpers', () => {
  it.each([
    ['/api/customers?page=1&pageSize=25', true],
    ['/ai/chat', true],
    ['https://evil.test/api/x', false],
    ['//evil.test/api/x', false],
    ['/api/../admin', false],
    ['/admin', false],
    ['/api/x\\y', false],
    ['/api/x\n', false],
  ])('isAllowedPath(%s) = %s', (p, ok) => expect(isAllowedPath(p)).toBe(ok));

  it('reads the refresh cookie and detects deletion', () => {
    expect(readCookie([refreshCookie('abc')], 'erp_refresh')).toBe('abc');
    expect(readCookie(['other=1', refreshCookie('abc')], 'erp_refresh')).toBe('abc');
    expect(readCookie(['erp_refresh=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/api/auth'], 'erp_refresh')).toBe('');
    expect(readCookie(['other=1'], 'erp_refresh')).toBeNull();
  });
});

describe('ApiClient', () => {
  it('logs in, keeps the token in main and stores the refresh cookie encrypted', async () => {
    const { client, store, fetchMock } = setup(() => json(authBody('jwt-1'), 200, [refreshCookie('r1')]));
    const res = await client.login('admin@demo.com', 'Admin@123');
    expect(res.status).toBe(200);
    expect(res.body).toEqual({ user, sessionExpiresAt: '2030-01-01T00:00:00Z', persistent: true });
    expect(JSON.stringify(res.body)).not.toContain('jwt-1'); // nothing secret goes to the renderer
    expect(store.save).toHaveBeenCalledWith('r1');
    expect(fetchMock.mock.calls[0][0]).toBe(`${BASE}/api/auth/login`);
  });

  it('returns the server problem on bad credentials and stores nothing', async () => {
    const { client, store } = setup(() => json({ title: 'Invalid email or password.' }, 401));
    const res = await client.login('admin@demo.com', 'nope');
    expect(res).toEqual({ status: 401, body: { title: 'Invalid email or password.' } });
    expect(store.save).not.toHaveBeenCalled();
    expect(client.currentUser).toBeNull();
  });

  it('passes a two-factor challenge to the UI without a session, then signs in with the code', async () => {
    const { client, store, fetchMock } = setup((_url, init) => {
      const body = JSON.parse(String(init.body));
      return body.code === '123456' ? json(authBody('jwt-2fa'), 200, [refreshCookie('r2')]) : json({ twoFactorRequired: true });
    });
    expect(await client.login('admin@demo.com', 'Admin@123')).toEqual({ status: 200, body: { twoFactorRequired: true } });
    expect(store.save).not.toHaveBeenCalled();
    expect(client.currentUser).toBeNull();

    const res = await client.login('admin@demo.com', 'Admin@123', '123456');
    expect(res.body).toEqual({ user, sessionExpiresAt: '2030-01-01T00:00:00Z', persistent: true });
    expect(store.save).toHaveBeenCalledWith('r2');
    expect(JSON.parse(String(fetchMock.mock.calls[1][1]!.body)))
      .toEqual({ email: 'admin@demo.com', password: 'Admin@123', code: '123456', rememberDevice: false });
  });

  it('"remember this device": keeps the trust cookie encrypted, sends it on the next login and keeps it after sign-out', async () => {
    const trust = new MemoryStore();
    const fetchMock = vi.fn(async (url: string | URL | Request, init?: RequestInit) => {
      const body = init?.body ? JSON.parse(String(init.body)) : {};
      if (String(url).endsWith('/logout')) return json(null, 204);
      if (body.rememberDevice) {
        return json(authBody('jwt-a'), 200, [refreshCookie('r1'), 'erp_2fa_trust=t-1; expires=Wed, 01 Jan 2031 00:00:00 GMT; path=/api/auth; httponly']);
      }
      const cookie = (init?.headers as Record<string, string>).Cookie ?? '';
      return cookie.includes('erp_2fa_trust=t-1') ? json(authBody('jwt-b'), 200, [refreshCookie('r2')]) : json({ twoFactorRequired: true });
    });
    const client = new ApiClient({ baseUrl: () => BASE, store: new MemoryStore(), trustStore: trust, fetch: fetchMock as unknown as typeof fetch });

    await client.login('admin@demo.com', 'Admin@123', '123456', true);
    expect(trust.save).toHaveBeenCalledWith('t-1');
    await client.logout();
    expect(trust.token).toBe('t-1'); // sign-out keeps the device remembered

    const res = await client.login('admin@demo.com', 'Admin@123'); // no code needed
    expect(res.body).toMatchObject({ user });
    const lastCookie = (fetchMock.mock.calls.at(-1)![1]!.headers as Record<string, string>).Cookie;
    expect(lastCookie).toBe('erp_2fa_trust=t-1'); // never the refresh token on login
  });

  it('forgets the stored trust token when the server deletes the cookie', async () => {
    const trust = new MemoryStore('old');
    const fetchMock = vi.fn(async () =>
      json({ twoFactorRequired: true }, 200, ['erp_2fa_trust=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/api/auth']));
    const client = new ApiClient({ baseUrl: () => BASE, store: new MemoryStore(), trustStore: trust, fetch: fetchMock as unknown as typeof fetch });
    await client.login('a@b.c', 'p');
    expect(trust.token).toBeNull();
  });

  it('rejects an oversized two-factor code without calling the server', async () => {
    const { client, fetchMock } = setup(() => json({}));
    expect((await client.login('a@b.c', 'p', 'x'.repeat(33))).status).toBe(400);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('attaches the bearer token to API calls', async () => {
    const { client, header } = setup((url) =>
      url.endsWith('/login') ? json(authBody('jwt-1'), 200, [refreshCookie('r1')]) : json({ ok: 1 }));
    await client.login('a@b.c', 'p');
    expect(await client.request('GET', '/api/dashboard/stats')).toEqual({ status: 200, body: { ok: 1 } });
    expect(header(1, 'Authorization')).toBe('Bearer jwt-1');
    expect(header(1, 'Cookie')).toBeUndefined(); // the refresh token only goes to /api/auth
  });

  it('blocks absolute URLs, odd methods and direct calls to auth endpoints', async () => {
    const { client, fetchMock } = setup(() => json({}));
    expect((await client.request('GET', 'https://evil.test/x')).status).toBe(400);
    expect((await client.request('TRACE', '/api/x')).status).toBe(400);
    expect((await client.request('POST', '/api/auth/refresh')).status).toBe(400);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('refreshes on 401 with the stored cookie, rotates it and retries once', async () => {
    const store = new MemoryStore('r-old');
    const { client, header, fetchMock } = setup((url, init) => {
      if (url.endsWith('/api/auth/refresh')) return json(authBody('jwt-new'), 200, [refreshCookie('r-new')]);
      const auth = (init.headers as Record<string, string>).Authorization;
      return auth === 'Bearer jwt-new' ? json({ ok: 1 }) : json({}, 401);
    }, store);
    expect(await client.restore()).toMatchObject({ user });
    expect(header(0, 'Cookie')).toBe('erp_refresh=r-old');
    expect(store.token).toBe('r-new');
    expect(await client.request('GET', '/api/x')).toEqual({ status: 200, body: { ok: 1 } });
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('shares one refresh between concurrent 401s', async () => {
    let token = 'jwt-1';
    const { client, fetchMock } = setup((url, init) => {
      if (url.endsWith('/login')) return json(authBody('jwt-1'), 200, [refreshCookie('r1')]);
      if (url.endsWith('/refresh')) { token = 'jwt-2'; return json(authBody('jwt-2'), 200, [refreshCookie('r2')]); }
      return (init.headers as Record<string, string>).Authorization === `Bearer ${token}` && token === 'jwt-2'
        ? json({ ok: 1 }) : json({}, 401);
    });
    await client.login('a@b.c', 'p');
    const results = await Promise.all([client.request('GET', '/api/a'), client.request('GET', '/api/b'), client.request('GET', '/api/c')]);
    expect(results.every((r) => r.status === 200)).toBe(true);
    expect(fetchMock.mock.calls.filter(([u]) => String(u).endsWith('/refresh'))).toHaveLength(1);
  });

  it('signs out (and tells the UI) when the refresh token is rejected', async () => {
    const { client, store, onSignedOut, fetchMock } = setup((url) =>
      url.endsWith('/login') ? json(authBody('jwt-1'), 200, [refreshCookie('r1')]) : json({}, 401));
    await client.login('a@b.c', 'p');
    expect((await client.request('GET', '/api/x')).status).toBe(401);
    expect(fetchMock).toHaveBeenCalledTimes(3); // login, call, one refresh; no loop
    expect(store.token).toBeNull();
    expect(client.currentUser).toBeNull();
    expect(onSignedOut).toHaveBeenCalledOnce();
  });

  it('keeps the refresh token when the server is unreachable', async () => {
    const store = new MemoryStore('r1');
    const { client } = setup(() => { throw new TypeError('fetch failed'); }, store);
    expect(await client.restore()).toBeNull();
    expect(store.token).toBe('r1'); // try again when the network is back
    expect(await client.request('GET', '/api/x')).toEqual({ status: 0, body: { title: 'Cannot reach the ERP server' } });
  });

  it('restore() returns null without a stored token and makes no request', async () => {
    const { client, fetchMock } = setup(() => json({}));
    expect(await client.restore()).toBeNull();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('logout revokes the session on the server and wipes local state', async () => {
    const { client, store, header, onSignedOut } = setup((url) =>
      url.endsWith('/login') ? json(authBody('jwt-1'), 200, [refreshCookie('r1')]) : json(null, 204));
    await client.login('a@b.c', 'p');
    await client.logout();
    expect(header(1, 'Cookie')).toBe('erp_refresh=r1');
    expect(header(1, 'Authorization')).toBe('Bearer jwt-1');
    expect(store.token).toBeNull();
    expect(client.currentUser).toBeNull();
    expect(onSignedOut).not.toHaveBeenCalled(); // user-initiated
  });
});

describe('openStream (Messages live events)', () => {
  const streamRes = (status = 200) => new Response(status === 200 ? 'event: ready\ndata: {}\n\n' : null, {
    status, headers: { 'Content-Type': 'text/event-stream' },
  });

  it('sends the access token from the main process and refreshes it once on 401', async () => {
    let streams = 0;
    const { client, fetchMock, header } = setup((url) => {
      if (url.endsWith('/api/auth/login')) return json(authBody('t1'), 200, [refreshCookie('r1')]);
      if (url.endsWith('/api/auth/refresh')) return json(authBody('t2'), 200, [refreshCookie('r2')]);
      if (url.endsWith('/api/chat/stream')) return streamRes(++streams === 1 ? 401 : 200);
      return json({}, 404);
    });
    await client.login('admin@demo.com', 'pw');
    const res = await client.openStream('/api/chat/stream', new AbortController().signal);
    expect(res.status).toBe(200);
    expect(res.body).not.toBeNull();
    expect(header(1, 'Authorization')).toBe('Bearer t1');
    expect(header(3, 'Authorization')).toBe('Bearer t2');
    expect(header(3, 'Accept')).toBe('text/event-stream');
    expect(fetchMock).toHaveBeenCalledTimes(4);
  });

  it('refuses paths outside /api and /ai and needs a session', async () => {
    const { client, fetchMock } = setup(() => json({}));
    expect((await client.openStream('https://evil.test/x', new AbortController().signal)).status).toBe(400);
    expect((await client.openStream('/api/chat/stream', new AbortController().signal)).status).toBe(401);
    expect(fetchMock).not.toHaveBeenCalled();
  });
});
