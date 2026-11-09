import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route, Routes } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import type { MyPagePermission } from '../api/client';
import { Layout } from '../components/Layout';
import { buildMenu } from '../components/menu';
import { ComingSoonRoute } from '../pages/ComingSoonPage';
import { jsonResponse, renderWithProviders, signIn } from '../test/utils';
import { PermissionsProvider, RequirePage } from './PermissionsContext';

const MODS: Record<string, [string, string, number]> = {
  master: ['Master', 'settings', 10], inventory: ['Inventory', 'box', 20],
};
const GROUPS: Record<string, [string, string, number]> = {
  'master-users': ['master', 'Users', 10], 'master-customers': ['master', 'Customers', 20],
  'inventory-stock-position': ['inventory', 'Stock Position', 10],
};

/** page('customers', 'Customers', '/master/customers', 'master-customers', { canRead: true }) */
function page(pageKey: string, name: string, route: string, groupKey: string | null,
  rights: Partial<MyPagePermission> = {}, sortOrder = 10): MyPagePermission {
  const g = groupKey ? GROUPS[groupKey] : null;
  const m = g ? MODS[g[0]] : null;
  return {
    pageKey, name, route, isImplemented: true,
    menu: {
      moduleKey: g?.[0] ?? null, module: m?.[0] ?? null, moduleIcon: m?.[1] ?? null, moduleSort: m?.[2] ?? 0,
      groupKey, group: g?.[1] ?? null, groupSort: g?.[2] ?? 0, sortOrder,
    },
    isVisible: true, isRestricted: false, canRead: true, canWrite: false, canUpdate: false, canDelete: false, ...rights,
  };
}

const ALL = [
  page('dashboard', 'Dashboard', '/', null),
  page('master-users', 'Users', '/master/users', 'master-users', {}, 10),
  page('master-user-rights', 'User Rights', '/master/user-rights', 'master-users', {}, 20),
  page('customers', 'Customers', '/master/customers', 'master-customers'),
  page('stock-position', 'Stock Position', '/inventory/stock-position', 'inventory-stock-position', { isImplemented: false }),
];

function mockPermissions(pages: MyPagePermission[]) {
  vi.spyOn(globalThis, 'fetch').mockImplementation(async () => jsonResponse({ role: 'User', pages }));
}

function App() {
  return (
    <PermissionsProvider>
      <Routes>
        <Route element={<Layout />}>
          <Route index element={<RequirePage page="dashboard"><h1>Dash</h1></RequirePage>} />
          <Route path="master/users" element={<RequirePage page="master-users"><h1>Users admin</h1></RequirePage>} />
          <Route path="master/customers" element={<RequirePage page="customers"><h1>Customers list</h1></RequirePage>} />
          <Route path="*" element={<ComingSoonRoute />} />
        </Route>
      </Routes>
    </PermissionsProvider>
  );
}

describe('buildMenu', () => {
  it('builds Module > Group > Page in order and drops empty branches', () => {
    const tree = buildMenu([
      ...ALL,
      page('vendors', 'Vendors', '/master/vendors', 'master-customers', { isRestricted: true }),
      page('stock-addition', 'Stock Addition', '/x', 'inventory-stock-position', { isVisible: false }),
    ]);
    expect(tree.top.map((p) => p.name)).toEqual(['Dashboard']);
    expect(tree.modules.map((m) => m.name)).toEqual(['Master', 'Inventory']);
    expect(tree.modules[0].groups.map((g) => g.name)).toEqual(['Users', 'Customers']);
    expect(tree.modules[0].groups[0].pages.map((p) => p.name)).toEqual(['Users', 'User Rights']);
    expect(tree.modules[0].groups[1].pages.map((p) => p.name)).toEqual(['Customers']); // restricted Vendors dropped
  });

  it('hides a whole module when the user has no page in it', () => {
    const tree = buildMenu([page('dashboard', 'Dashboard', '/', null),
      page('stock-position', 'Stock Position', '/s', 'inventory-stock-position', { canRead: false })]);
    expect(tree.modules).toEqual([]);
  });
});

describe('3-level side menu', () => {
  it('expands Module then Group to reveal pages; current page branch opens automatically', async () => {
    signIn();
    mockPermissions(ALL);
    renderWithProviders(<App />, '/master/users');

    const nav = await screen.findByRole('navigation', { name: 'Main menu' });
    // active branch Master > Users is open on load
    expect(await within(nav).findByRole('link', { name: 'User Rights' })).toBeInTheDocument();
    expect(within(nav).getByRole('button', { name: 'Master' })).toHaveAttribute('aria-expanded', 'true');

    // Inventory is closed until clicked, then its group, then the page shows
    expect(within(nav).queryByRole('link', { name: /Stock Position/ })).not.toBeInTheDocument();
    await userEvent.click(within(nav).getByRole('button', { name: 'Inventory' }));
    await userEvent.click(within(nav).getByRole('button', { name: 'Stock Position' }));
    expect(within(nav).getByRole('link', { name: /Stock Position/ })).toHaveTextContent('soon');
  });

  it('hamburger opens and closes the drawer (mobile)', async () => {
    signIn();
    mockPermissions(ALL);
    renderWithProviders(<App />, '/');
    const burger = await screen.findByRole('button', { name: 'Open menu' });
    expect(burger).toHaveAttribute('aria-expanded', 'false');

    await userEvent.click(burger);
    expect(screen.getByRole('navigation', { name: 'Main menu' })).toHaveClass('open');
    expect(screen.getByRole('button', { name: 'Close menu' })).toHaveAttribute('aria-expanded', 'true');

    await userEvent.keyboard('{Escape}');
    expect(screen.getByRole('navigation', { name: 'Main menu' })).not.toHaveClass('open');
  });

  it('navigating from the drawer closes it', async () => {
    signIn();
    mockPermissions(ALL);
    renderWithProviders(<App />, '/');
    await userEvent.click(await screen.findByRole('button', { name: 'Open menu' }));
    const nav = screen.getByRole('navigation', { name: 'Main menu' });
    await userEvent.click(within(nav).getByRole('button', { name: 'Master' }));
    await userEvent.click(within(nav).getByRole('button', { name: 'Customers' }));
    await userEvent.click(within(nav).getByRole('link', { name: 'Customers' }));
    expect(await screen.findByRole('heading', { name: 'Customers list' })).toBeInTheDocument();
    expect(nav).not.toHaveClass('open');
  });
});

describe('page access', () => {
  it('blocks a page the user may not read, even when opened by URL', async () => {
    signIn();
    mockPermissions([page('dashboard', 'Dashboard', '/', null)]);
    renderWithProviders(<App />, '/master/users');
    expect(await screen.findByRole('heading', { name: 'No access' })).toBeInTheDocument();
  });

  it('restricted is a hard block even with canRead', async () => {
    signIn();
    mockPermissions([page('customers', 'Customers', '/master/customers', 'master-customers', { isRestricted: true })]);
    renderWithProviders(<App />, '/master/customers');
    expect(await screen.findByRole('heading', { name: 'No access' })).toBeInTheDocument();
  });

  it('a page without a screen yet shows Coming soon with its breadcrumb', async () => {
    signIn();
    mockPermissions(ALL);
    renderWithProviders(<App />, '/inventory/stock-position');
    expect(await screen.findByRole('heading', { name: 'Coming soon' })).toBeInTheDocument();
    expect(screen.getByText('Inventory › Stock Position › Stock Position')).toBeInTheDocument();
  });
});
