import { describe, expect, it, vi } from 'vitest';
import { ApiError, request, setUnauthorizedHandler, TOKEN_KEY } from './client';
import { jsonResponse } from '../test/utils';

describe('request()', () => {
  it('attaches bearer token when present', async () => {
    localStorage.setItem(TOKEN_KEY, 'abc');
    const f = vi.spyOn(globalThis, 'fetch').mockResolvedValue(jsonResponse({ ok: 1 }));
    await request('/api/x');
    const headers = f.mock.calls[0][1]!.headers as Headers;
    expect(headers.get('Authorization')).toBe('Bearer abc');
  });

  it('omits Authorization header when logged out', async () => {
    const f = vi.spyOn(globalThis, 'fetch').mockResolvedValue(jsonResponse({}));
    await request('/api/x');
    expect((f.mock.calls[0][1]!.headers as Headers).has('Authorization')).toBe(false);
  });

  it('throws ApiError with server problem title', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(jsonResponse({ title: 'Insufficient stock' }, 400));
    await expect(request('/api/orders')).rejects.toMatchObject({ status: 400, message: 'Insufficient stock' });
  });

  it('uses FastAPI "detail" field for errors', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(jsonResponse({ detail: 'Rate limit exceeded' }, 429));
    const err = (await request('/ai/chat').catch((e: unknown) => e)) as ApiError;
    expect(err).toBeInstanceOf(ApiError);
    expect(err.status).toBe(429);
  });

  it('calls unauthorized handler on 401', async () => {
    const handler = vi.fn();
    setUnauthorizedHandler(handler);
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(jsonResponse({}, 401));
    await request('/api/x').catch(() => undefined);
    expect(handler).toHaveBeenCalledOnce();
    setUnauthorizedHandler(null);
  });

  it('returns undefined for 204', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(null, { status: 204 }));
    await expect(request('/api/x', { method: 'DELETE' })).resolves.toBeUndefined();
  });

  it('refreshes the session on 401 and retries once with the new token', async () => {
    localStorage.setItem(TOKEN_KEY, 'expired');
    const f = vi.spyOn(globalThis, 'fetch')
      .mockResolvedValueOnce(jsonResponse({}, 401))                                    // original call
      .mockResolvedValueOnce(jsonResponse({ accessToken: 'fresh', expiresAt: 'x', user: { id: 'u' } })) // refresh
      .mockResolvedValueOnce(jsonResponse({ ok: 1 }));                                 // retry
    await expect(request('/api/x')).resolves.toEqual({ ok: 1 });
    expect(f.mock.calls[1][0]).toBe('/api/auth/refresh');
    expect((f.mock.calls[2][1]!.headers as Headers).get('Authorization')).toBe('Bearer fresh');
    expect(localStorage.getItem(TOKEN_KEY)).toBe('fresh');
  });

  it('signs out when the refresh is also rejected', async () => {
    localStorage.setItem(TOKEN_KEY, 'expired');
    const handler = vi.fn();
    setUnauthorizedHandler(handler);
    const f = vi.spyOn(globalThis, 'fetch').mockResolvedValue(jsonResponse({}, 401));
    await expect(request('/api/x')).rejects.toMatchObject({ status: 401 });
    expect(f).toHaveBeenCalledTimes(2); // original + one refresh, no loop
    expect(handler).toHaveBeenCalledOnce();
    setUnauthorizedHandler(null);
  });

  it('does not try to refresh for auth endpoints (e.g. wrong password)', async () => {
    const f = vi.spyOn(globalThis, 'fetch').mockResolvedValue(jsonResponse({ title: 'Invalid' }, 401));
    await request('/api/auth/login', { method: 'POST' }).catch(() => undefined);
    expect(f).toHaveBeenCalledOnce();
  });

  it('shares one refresh between concurrent 401s', async () => {
    localStorage.setItem(TOKEN_KEY, 'expired');
    const f = vi.spyOn(globalThis, 'fetch').mockImplementation(async (input, init) => {
      if (input === '/api/auth/refresh') return jsonResponse({ accessToken: 'fresh', expiresAt: 'x', user: {} });
      const auth = (init!.headers as Headers).get('Authorization');
      return auth === 'Bearer fresh' ? jsonResponse({ ok: 1 }) : jsonResponse({}, 401);
    });
    await Promise.all([request('/api/a'), request('/api/b'), request('/api/c')]);
    expect(f.mock.calls.filter(([u]) => u === '/api/auth/refresh')).toHaveLength(1);
  });
});
