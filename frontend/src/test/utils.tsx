import { render } from '@testing-library/react';
import type { ReactElement } from 'react';
import { MemoryRouter } from 'react-router-dom';
import { vi } from 'vitest';
import { AuthProvider } from '../auth/AuthContext';
import { PermissionsProvider } from '../auth/PermissionsContext';
import { TOKEN_KEY, type MyPagePermission, type UserDto } from '../api/client';

export const demoUser: UserDto = {
  id: 'u1', email: 'admin@demo.com', fullName: 'Demo Admin', role: 'Admin',
  organizationId: '11111111-1111-1111-1111-111111111111',
};

export function signIn(user: UserDto = demoUser) {
  localStorage.setItem(TOKEN_KEY, 'test-token');
  localStorage.setItem('erp.user', JSON.stringify(user));
}

export function renderWithProviders(ui: ReactElement, route = '/') {
  return render(
    <MemoryRouter initialEntries={[route]}>
      <AuthProvider>{ui}</AuthProvider>
    </MemoryRouter>,
  );
}

export function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

// ---------- Screen-test helpers ----------
type Responder = (body: unknown, url: string) => unknown;
/** [method, url or prefix (ends with *), response body | function | Response] */
export type Route = [string, string, unknown | Responder | Response];

/**
 * Fake backend: answers fetch() by method + URL from `routes` (first match wins), 404 otherwise.
 * Returns the spy; `calls(spy)` lists "METHOD url" strings for assertions.
 */
export function mockApi(routes: Route[]) {
  return vi.spyOn(globalThis, 'fetch').mockImplementation(async (input, init) => {
    const url = String(input);
    const method = (init?.method ?? 'GET').toUpperCase();
    const body = init?.body ? JSON.parse(String(init.body)) : undefined;
    const hit = routes.find(([m, u]) => m === method && (u.endsWith('*') ? url.startsWith(u.slice(0, -1)) : url === u));
    if (!hit) return jsonResponse({ title: `No mock for ${method} ${url}` }, 404);
    const r = hit[2];
    if (r instanceof Response) return r.clone();
    const value = typeof r === 'function' ? (r as Responder)(body, url) : r;
    if (value instanceof Response) return value;
    return value === undefined ? new Response(null, { status: 204 }) : jsonResponse(value);
  });
}

export const calls = (spy: ReturnType<typeof mockApi>) =>
  spy.mock.calls.map(([u, init]) => `${(init?.method ?? 'GET').toUpperCase()} ${String(u)}`);

/** One page's rights for /api/me/permissions. rights: any of "VRWUD" (visible, read, write, update, delete). */
export function perm(pageKey: string, rights = 'VR', extra: Partial<MyPagePermission> = {}): MyPagePermission {
  return {
    pageKey, name: pageKey, route: `/${pageKey}`, isImplemented: true,
    menu: { moduleKey: null, module: null, moduleIcon: null, moduleSort: 0, groupKey: null, group: null, groupSort: 0, sortOrder: 10 },
    isVisible: rights.includes('V'), isRestricted: false, canRead: rights.includes('R'),
    canWrite: rights.includes('W'), canUpdate: rights.includes('U'), canDelete: rights.includes('D'), ...extra,
  };
}

/** Renders a screen inside PermissionsProvider; `pages` is what /api/me/permissions returns. */
export function renderScreen(ui: ReactElement, pages: MyPagePermission[], routes: Route[] = [], route = '/') {
  const spy = mockApi([['GET', '/api/me/permissions', { role: 'Admin', pages }], ...routes]);
  signIn();
  const view = renderWithProviders(<PermissionsProvider>{ui}</PermissionsProvider>, route);
  return { ...view, fetch: spy };
}
