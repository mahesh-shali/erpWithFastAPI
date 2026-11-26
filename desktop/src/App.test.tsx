import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import App from './App';
import { fakeState } from './test/fakeBridge';
import { calls, jsonResponse, mockApi, perm, renderWithProviders, signIn, type Route } from './test/utils';

/** Every screen's data endpoint answers with empty data; only routing and guards are under test. */
const EMPTY: Route[] = [
  ['GET', '/api/chat/keys/me', undefined],
  ['GET', '/api/dashboard/stats', { customers: 1, products: 2, orders: 3, revenue: 4, lowStockProducts: 0 }],
  ['GET', '/api/analytics*', jsonResponse({ title: 'not needed' }, 500)],
  ['GET', '/api/customers*', { items: [], page: 1, pageSize: 25, total: 0 }],
  ['GET', '/api/products*', { items: [], page: 1, pageSize: 25, total: 0 }],
  ['GET', '/api/orders*', []],
  ['GET', '/api/users', []],
  ['GET', '/api/roles/assignable', []],
  ['GET', '/api/roles', []],
  ['GET', '/api/master/pages', []],
  ['GET', '/api/uoms*', []],
  ['GET', '/api/stores*', []],
  ['GET', '/api/stock/position*', { items: [], page: 1, pageSize: 50, total: 0, totalQuantity: 0, totalValue: 0 }],
  ['GET', '/api/stock/ledger*', { items: [], page: 1, pageSize: 50, total: 0 }],
  ['GET', '/api/stock/details*', { items: [], page: 1, pageSize: 50, total: 0 }],
  ['GET', '/api/stock/*', []],
  ['GET', '/api/stock-entries*', { items: [], page: 1, pageSize: 25, total: 0 }],
  ['GET', '/api/customer-pos*', { items: [], page: 1, pageSize: 25, total: 0 }],
  ['GET', '/api/work-orders*', { items: [], page: 1, pageSize: 25, total: 0 }],
  ['GET', '/api/sales-invoices*', { items: [], page: 1, pageSize: 25, total: 0 }],
  ['GET', '/api/sales/lookups/*', []],
  ['POST', '/api/auth/logout', undefined],
];

const ALL_PAGES = [
  perm('dashboard', 'VR', { name: 'Dashboard', route: '/' }),
  perm('analytics', 'VR', { name: 'Analytics', route: '/analytics' }),
  perm('ai-chat', 'VR', { name: 'AI Assistant', route: '/assistant' }),
  perm('messages', 'VRW', { name: 'Messages', route: '/messages' }),
  perm('customers', 'VR', { name: 'Customers', route: '/master/customers' }),
  perm('products', 'VR', { name: 'Items', route: '/master/items' }),
  perm('customer-pos', 'VRW', { name: 'Customer P.O.', route: '/sales/customer-po' }),
  perm('work-orders', 'VRW', { name: 'Work Orders', route: '/production/work-orders' }),
  perm('sales-invoices', 'VRW', { name: 'Sales Invoices', route: '/sales/invoices' }),
  perm('stock-details', 'VR', { name: 'Stock Details', route: '/inventory/stock-details' }),
  perm('uom', 'VR', { name: 'Units of Measure', route: '/master/uom' }),
  perm('stores', 'VR', { name: 'Stores', route: '/master/stores' }),
  perm('stock-addition', 'VR', { name: 'Stock Entries', route: '/inventory/stock-addition' }),
  perm('stock-position', 'VR', { name: 'Stock Position', route: '/inventory/stock-position' }),
  perm('stock-report', 'VR', { name: 'Stock Ledger', route: '/reports/stock' }),
  perm('orders', 'VR', { name: 'Sales Orders', route: '/sales/orders' }),
  perm('master-users', 'VR', { name: 'Users', route: '/master/users' }),
  perm('master-user-rights', 'VR', { name: 'User Rights', route: '/master/user-rights' }),
  perm('master-role-rights', 'VR', { name: 'Role Rights', route: '/master/role-rights' }),
  perm('master-pages', 'VR', { name: 'Pages', route: '/master/pages' }),
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
    ['/master/items', 'Items'],
    ['/master/products', 'Items'], // old link: Products became Items
    ['/sales/customer-po', 'Customer P.O.'],
    ['/sales/customer-po/new', 'New customer P.O.'],
    ['/production/work-orders', 'Work orders'],
    ['/production/work-orders/new', 'New work order'],
    ['/sales/invoices', 'Sales invoices'],
    ['/sales/invoices/new', 'New invoice'],
    ['/inventory/stock-details', 'Stock details'],
    ['/master/uom', 'Units of measure'],
    ['/master/stores', 'Stores'],
    ['/inventory/stock-addition', 'Stock entries'],
    ['/inventory/stock-addition/new', 'New stock entry'],
    ['/inventory/stock-position', 'Stock position'],
    ['/reports/stock', 'Stock ledger'],
    ['/sales/orders', 'Orders'],
    ['/master/users', 'Users'],
    ['/master/user-rights', 'User rights'],
    ['/master/role-rights', 'Role rights'],
    ['/master/pages', 'Settings'], // the Pages master moved to Settings › Pages
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
    expect(fakeState.user).toBeNull();
    expect(await screen.findByRole('button', { name: /sign in|log in/i })).toBeInTheDocument();
  });
});

describe('Reopens where the user left off', () => {
  const KEY = 'erp:last-page';

  it('goes back to the last page of this user after a launch, without flashing the Dashboard', async () => {
    localStorage.setItem(KEY, JSON.stringify({ u1: '/master/customers' }));
    const spy = renderApp('/');
    expect(await screen.findByRole('heading', { level: 1, name: 'Customers' })).toBeInTheDocument();
    expect(calls(spy)).not.toContain('GET /api/dashboard/stats'); // the start page never rendered
  });

  it("does not open another user's last page", async () => {
    localStorage.setItem(KEY, JSON.stringify({ someoneElse: '/master/customers' }));
    renderApp('/');
    expect(await screen.findByRole('heading', { level: 1, name: 'Dashboard' })).toBeInTheDocument();
  });

  it('remembers the page the user opens', async () => {
    renderApp('/analytics');
    expect(await screen.findByRole('heading', { level: 1, name: 'Analytics' })).toBeInTheDocument();
    await waitFor(() => expect(JSON.parse(localStorage.getItem(KEY) ?? '{}')).toEqual({ u1: '/analytics' }));
  });

  it('restores only once per launch, so Dashboard stays reachable', async () => {
    localStorage.setItem(KEY, JSON.stringify({ u1: '/master/customers' }));
    renderApp('/');
    expect(await screen.findByRole('heading', { level: 1, name: 'Customers' })).toBeInTheDocument();
    await userEvent.click(screen.getByRole('link', { name: 'Dashboard' }));
    expect(await screen.findByRole('heading', { level: 1, name: 'Dashboard' })).toBeInTheDocument();
  });

  it('ignores a stored value that is not an app path', async () => {
    localStorage.setItem(KEY, JSON.stringify({ u1: '//evil.example/x' }));
    renderApp('/');
    expect(await screen.findByRole('heading', { level: 1, name: 'Dashboard' })).toBeInTheDocument();
  });
});

describe('Sign-in screen', () => {
  it('shows the mode but not the server URL', async () => {
    renderWithProviders(<App />, '/login');
    expect(await screen.findByText('Development')).toBeInTheDocument();
    expect(screen.queryByText(/localhost|https?:\/\//)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Change' })).not.toBeInTheDocument();
  });

  it('labels production mode', async () => {
    fakeState.settings = { ...fakeState.settings, environment: 'production', serverUrl: 'https://erp.example.com' };
    renderWithProviders(<App />, '/login');
    expect(await screen.findByText('Production')).toBeInTheDocument();
  });
});
