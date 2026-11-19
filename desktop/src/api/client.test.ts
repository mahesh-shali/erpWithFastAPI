import { describe, expect, it, vi } from 'vitest';
import { ApiError, auth, request, setUnauthorizedHandler } from './client';

const bridge = () => window.erp;

describe('request() over the preload bridge', () => {
  it('forwards method, path and parsed JSON body to the main process', async () => {
    const spy = vi.spyOn(bridge().api, 'request').mockResolvedValue({ status: 200, body: { ok: 1 } });
    await expect(request('/api/x', { method: 'post', body: JSON.stringify({ a: 1 }) })).resolves.toEqual({ ok: 1 });
    expect(spy).toHaveBeenCalledWith('POST', '/api/x', { a: 1 });
  });

  it('never sees a token: the renderer only passes method, path and body', async () => {
    const spy = vi.spyOn(bridge().api, 'request').mockResolvedValue({ status: 200, body: {} });
    await request('/api/x');
    expect(spy.mock.calls[0]).toEqual(['GET', '/api/x', undefined]);
  });

  it('throws ApiError with server problem title', async () => {
    vi.spyOn(bridge().api, 'request').mockResolvedValue({ status: 400, body: { title: 'Insufficient stock' } });
    await expect(request('/api/orders')).rejects.toMatchObject({ status: 400, message: 'Insufficient stock' });
  });

  it('uses FastAPI "detail" field for errors', async () => {
    vi.spyOn(bridge().api, 'request').mockResolvedValue({ status: 429, body: { detail: 'Rate limit exceeded' } });
    const err = (await request('/ai/chat').catch((e: unknown) => e)) as ApiError;
    expect(err).toBeInstanceOf(ApiError);
    expect(err.message).toBe('Rate limit exceeded');
  });

  it('reports an unreachable server (status 0)', async () => {
    vi.spyOn(bridge().api, 'request').mockResolvedValue({ status: 0, body: null });
    await expect(request('/api/x')).rejects.toMatchObject({ status: 0, message: 'Cannot reach the ERP server' });
  });

  it('calls the unauthorized handler when the main process could not refresh (401)', async () => {
    const handler = vi.fn();
    setUnauthorizedHandler(handler);
    vi.spyOn(bridge().api, 'request').mockResolvedValue({ status: 401, body: {} });
    await request('/api/x').catch(() => undefined);
    expect(handler).toHaveBeenCalledOnce();
    setUnauthorizedHandler(null);
  });

  it('returns undefined for 204', async () => {
    vi.spyOn(bridge().api, 'request').mockResolvedValue({ status: 204, body: null });
    await expect(request('/api/x', { method: 'DELETE' })).resolves.toBeUndefined();
  });
});

describe('auth bridge', () => {
  it('login resolves the session or throws the server message', async () => {
    vi.spyOn(bridge().auth, 'login')
      .mockResolvedValueOnce({ status: 401, body: { title: 'Invalid email or password.' } as never })
      .mockResolvedValueOnce({ status: 200, body: { user: { id: 'u' } as never, sessionExpiresAt: null, persistent: true } });
    await expect(auth.login('a@b.c', 'x')).rejects.toMatchObject({ status: 401, message: 'Invalid email or password.' });
    await expect(auth.login('a@b.c', 'y')).resolves.toMatchObject({ user: { id: 'u' } });
  });
});
