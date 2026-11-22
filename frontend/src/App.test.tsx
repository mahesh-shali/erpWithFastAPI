import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import App from './App';
import { TOKEN_KEY } from './api/client';
import { calls, jsonResponse, mockApi, perm, renderWithProviders, signIn, type Route } from './test/utils';

/** Every screen's data endpoint answers with empty data; only routing and guards are under test. */
const EMPTY: Route[] = [
  ['GET', '/api/dashboard/stats', { customers: 1, products: 2, orders: 3, revenue: 4, lowStockProducts: 0 }],
  ['GET', '/api/analytics*', jsonResponse({ title: 'not needed' }, 500)],
  ['GET', '/api/customers*', { items: [], page: 1, pageSize: 25, total: 0 }],
  ['GET', '/api/products*', { items: [], page: 1, pageSize: 25, total: 0 }],
  ['GET', '/api/orders*', []],
  ['GET', '/api/users', []],
  ['GET', '/api/roles/assignable', []],
  ['GET', '/api/roles', []],
  ['GET', '/api/master/pages', []],
  ['POST', '/api/auth/logout', undefined],
  ['GET', '/api/uoms*', []],
  ['GET', '/api/stores*', []],
  ['GET', '/api/stock/*', []],
  ['GET', '/api/stock-entries*', { items: [], page: 1, pageSize: 25, total: 0 }],
  ['GET', '/api/chat/keys/me', undefined],
];

const ALL_PAGES = [
  perm('dashboard', 'VR', { name: 'Dashboard', route: '/' }),
  perm('analytics', 'VR', { name: 'Analytics', route: '/analytics' }),
  perm('ai-chat', 'VR', { name: 'AI Assistant', route: '/assistant' }),
  perm('messages', 'VRW', { name: 'Messages', route: '/messages' }),
  perm('customers', 'VR', { name: 'Customers', route: '/master/customers' }),
  perm('products', 'VR', { name: 'Products', route: '/master/products' }),
  perm('orders', 'VR', { name: 'Sales Orders', route: '/sales/orders' }),
  perm('master-users', 'VR', { name: 'Users', route: '/master/users' }),
  perm('master-user-rights', 'VR', { name: 'User Rights', route: '/master/user-rights' }),
  perm('master-role-rights', 'VR', { name: 'Role Rights', route: '/master/role-rights' }),
  perm('master-pages', 'VR', { name: 'Pages', route: '/master/pages' }),
  perm('uom', 'VR', { name: 'Units of Measure', route: '/master/uom' }),
  perm('stores', 'VR', { name: 'Stores', route: '/master/stores' }),
  perm('stock-addition', 'VR', { name: 'Stock Entries', route: '/inventory/stock-addition' }),
  perm('stock-position', 'VR', { name: 'Stock Position', route: '/inventory/stock-position' }),
  perm('stock-report', 'VR', { name: 'Stock Ledger', route: '/reports/stock' }),
  perm('vendors', 'VR', { name: 'Vendors', route: '/master/vendors', isImplemented: false }),
];

function renderApp(route: string, pages = ALL_PAGES) {
  signIn();
  const spy = mockApi([['GET', '/api/me/permissions', { role: 'Admin', pages }], ...EMPTY]);
  renderWithProviders(<App />, route);
  return spy;
}

describe('App routing', () => {
  it.each([
    ['/', 'Dashboard'],
    ['/analytics', 'Analytics'],
    ['/assistant', 'AI Assistant'],
    ['/messages', 'Messages'],
    ['/master/customers', 'Customers'],
    ['/master/products', 'Products'],
    ['/sales/orders', 'Orders'],
    ['/master/users', 'Users'],
    ['/master/user-rights', 'User rights'],
    ['/master/role-rights', 'Role rights'],
    ['/master/pages', 'Settings'], // the Pages master moved to Settings › Pages
    ['/master/uom', 'Units of measure'],
    ['/master/stores', 'Stores'],
    ['/inventory/stock-addition', 'Stock entries'],
    ['/inventory/stock-addition/new', 'New stock entry'],
    ['/inventory/stock-position', 'Stock position'],
    ['/reports/stock', 'Stock ledger'],
    ['/settings', 'Settings'],
    ['/settings/security', 'Settings'],
  ])('%s opens the %s screen', async (route, heading) => {
    renderApp(route);
    expect(await screen.findByRole('heading', { level: 1, name: heading })).toBeInTheDocument();
  });

  it('a page without a screen shows Coming soon', async () => {
    renderApp('/master/vendors');
    expect(await screen.findByRole('heading', { name: 'Coming soon' })).toBeInTheDocument();
  });

  it('an unknown route goes to the dashboard', async () => {
    renderApp('/no/such/page');
    expect(await screen.findByRole('heading', { level: 1, name: 'Dashboard' })).toBeInTheDocument();
  });

  it.each([
    ['/customer', 'Customers'],
    ['/user', 'Users'],
    ['/userrights', 'User rights'],
  ])('legacy V.SMART path %s redirects to %s', async (route, heading) => {
    renderApp(route);
    expect(await screen.findByRole('heading', { level: 1, name: heading })).toBeInTheDocument();
  });

  it('a screen the user may not read shows No access and never loads its data', async () => {
    const spy = renderApp('/analytics', ALL_PAGES.filter((p) => p.pageKey !== 'analytics'));
    expect(await screen.findByRole('heading', { name: 'No access' })).toBeInTheDocument();
    expect(calls(spy).some((c) => c.includes('/api/analytics'))).toBe(false);
  });

  it('signed-out users are sent to the login page', async () => {
    mockApi(EMPTY);
    renderWithProviders(<App />, '/master/customers');
    expect(await screen.findByRole('button', { name: /sign in|log in/i })).toBeInTheDocument();
  });

  it('Sign out ends the session on the server and returns to login', async () => {
    const spy = renderApp('/');
    await screen.findByRole('heading', { level: 1, name: 'Dashboard' });
    await userEvent.click(screen.getByRole('button', { name: 'Sign out' }));
    await waitFor(() => expect(calls(spy)).toContain('POST /api/auth/logout'));
    expect(localStorage.getItem(TOKEN_KEY)).toBeNull();
    expect(await screen.findByRole('button', { name: /sign in|log in/i })).toBeInTheDocument();
  });
});

describe('Reopens where the user left off', () => {
  const KEY = 'erp:last-page';
  const AUTH = { accessToken: 'jwt', expiresAt: '2030-01-01', user: { id: 'u1', email: 'admin@demo.com', fullName: 'Demo Admin',
    role: 'Admin', organizationId: '11111111-1111-1111-1111-111111111111' } };

  async function signInThroughTheForm() {
    await userEvent.type(screen.getByLabelText(/email/i), 'admin@demo.com');
    await userEvent.type(screen.getByLabelText(/password/i), 'Admin@123');
    await userEvent.click(screen.getByRole('button', { name: /sign in/i }));
  }

  it('after signing in, opens the last page of this user instead of the Dashboard', async () => {
    localStorage.setItem(KEY, JSON.stringify({ u1: '/master/customers' }));
    const spy = mockApi([['POST', '/api/auth/login', AUTH], ['GET', '/api/me/permissions', { role: 'Admin', pages: ALL_PAGES }], ...EMPTY]);
    renderWithProviders(<App />, '/login');
    await signInThroughTheForm();
    expect(await screen.findByRole('heading', { level: 1, name: 'Customers' })).toBeInTheDocument();
    expect(calls(spy)).not.toContain('GET /api/dashboard/stats'); // the Dashboard never rendered
  });

  it('with no remembered page, signing in shows the Dashboard', async () => {
    mockApi([['POST', '/api/auth/login', AUTH], ['GET', '/api/me/permissions', { role: 'Admin', pages: ALL_PAGES }], ...EMPTY]);
    renderWithProviders(<App />, '/login');
    await signInThroughTheForm();
    expect(await screen.findByRole('heading', { level: 1, name: 'Dashboard' })).toBeInTheDocument();
  });

  it('also reopens after sign out and sign in again in the same tab', async () => {
    renderApp('/master/products');
    expect(await screen.findByRole('heading', { level: 1, name: 'Products' })).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Sign out' }));
    mockApi([['POST', '/api/auth/login', AUTH], ['POST', '/api/auth/logout', undefined],
      ['GET', '/api/me/permissions', { role: 'Admin', pages: ALL_PAGES }], ...EMPTY]);
    await signInThroughTheForm();
    expect(await screen.findByRole('heading', { level: 1, name: 'Products' })).toBeInTheDocument();
  });

  it('a page load on the start page reopens the last page; the Dashboard stays reachable', async () => {
    localStorage.setItem(KEY, JSON.stringify({ u1: '/master/customers' }));
    renderApp('/');
    expect(await screen.findByRole('heading', { level: 1, name: 'Customers' })).toBeInTheDocument();
    await userEvent.click(screen.getByRole('link', { name: 'Dashboard' }));
    expect(await screen.findByRole('heading', { level: 1, name: 'Dashboard' })).toBeInTheDocument();
  });

  it("a direct link wins, and another user's page or a bad value is ignored", async () => {
    localStorage.setItem(KEY, JSON.stringify({ u1: '/master/customers' }));
    renderApp('/master/products');
    expect(await screen.findByRole('heading', { level: 1, name: 'Products' })).toBeInTheDocument();
  });

  it.each([[{ someoneElse: '/master/customers' }], [{ u1: '//evil.example/x' }]])('ignores %j', async (saved) => {
    localStorage.setItem(KEY, JSON.stringify(saved));
    renderApp('/');
    expect(await screen.findByRole('heading', { level: 1, name: 'Dashboard' })).toBeInTheDocument();
  });
});
