import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import type { OrgUser, PageRights, RoleRights, UserRights } from '../../api/client';
import { calls, jsonResponse, perm, renderScreen, type Route } from '../../test/utils';
import { PagesPage } from './PagesPage';
import { RoleRightsPage } from './RoleRightsPage';
import { UserRightsPage } from './UserRightsPage';
import { UsersPage } from './UsersPage';

const menu = { moduleKey: 'master', module: 'Master', moduleIcon: 'settings', moduleSort: 10,
  groupKey: 'master-customers', group: 'Customers', groupSort: 20, sortOrder: 10 };
const top = { ...menu, moduleKey: null, module: null, moduleIcon: null, groupKey: null, group: null };
const R = (s = ''): PageRights => ({
  isVisible: s.includes('V'), isRestricted: s.includes('X'), canRead: s.includes('R'),
  canWrite: s.includes('W'), canUpdate: s.includes('U'), canDelete: s.includes('D'),
});

const users: OrgUser[] = [
  { id: 'u1', email: 'admin@demo.com', fullName: 'Demo Admin', role: 'SuperAdmin', createdAt: '', lastLoginAt: '2026-11-16T09:00:00Z', canManage: false },
  { id: 'u2', email: 'staff@demo.com', fullName: 'Staff Person', role: 'User', createdAt: '', lastLoginAt: null, canManage: true },
];
const assignable = [{ id: 1, name: 'Admin', description: null, level: 50 }, { id: 2, name: 'User', description: null, level: 10 }];
const allRoles = [{ id: 3, name: 'SuperAdmin', description: null, level: 100 }, ...assignable];

describe('Master › Pages screen', () => {
  it('lists every page with module, group, key, route and status', async () => {
    renderScreen(<PagesPage />, [perm('master-pages')], [['GET', '/api/master/pages', [
      { id: 1, key: 'dashboard', name: 'Dashboard', route: '/', menu: top, isActive: true, isImplemented: true },
      { id: 10, key: 'vendors', name: 'Vendors', route: '/master/vendors', menu, isActive: true, isImplemented: false },
      { id: 99, key: 'old', name: 'Old page', route: '/old', menu, isActive: false, isImplemented: true },
    ]]]);
    const table = await screen.findByRole('table', { name: 'Pages' });
    const dash = (await within(table).findByText('Dashboard')).closest('tr')!;
    expect(dash).toHaveTextContent('—');
    expect(dash).toHaveTextContent('Ready');
    expect(within(table).getByText('Vendors').closest('tr')).toHaveTextContent('Coming soon');
    expect(within(table).getByText('Old page').closest('tr')).toHaveTextContent('Inactive');
    expect(within(table).getByText('vendors').tagName).toBe('CODE');
  });

  it('shows the load error', async () => {
    renderScreen(<PagesPage />, [perm('master-pages')], [['GET', '/api/master/pages', jsonResponse({ title: 'Forbidden' }, 403)]]);
    expect(await screen.findByRole('alert')).toHaveTextContent('Forbidden');
  });
});

describe('Master › Users screen', () => {
  const routes = (list = users): Route[] => [
    ['GET', '/api/users', () => list],
    ['GET', '/api/roles/assignable', assignable],
  ];

  it('lists users; only manageable users get a role picker and Delete', async () => {
    renderScreen(<UsersPage />, [perm('master-users', 'VRWUD'), perm('master-user-rights', 'VR')], [...routes()]);
    const staff = (await screen.findByText('Staff Person')).closest('tr')!;
    expect(within(staff).getByRole('combobox', { name: 'Role of Staff Person' })).toHaveValue('User');
    expect(within(staff).getByRole('button', { name: 'Delete' })).toBeInTheDocument();
    expect(within(staff).getByText('Never')).toBeInTheDocument();
    expect(within(staff).getByRole('link', { name: 'Rights' })).toHaveAttribute('href', '/master/user-rights?user=u2');

    const admin = screen.getByText('Demo Admin').closest('tr')!;
    expect(within(admin).queryByRole('combobox')).not.toBeInTheDocument();
    expect(within(admin).getByText('SuperAdmin')).toHaveClass('role-superadmin');
    expect(within(admin).queryByRole('button', { name: 'Delete' })).not.toBeInTheDocument();
  });

  it('read-only rights: no create form, no role picker, no Delete, no Rights link', async () => {
    renderScreen(<UsersPage />, [perm('master-users', 'VR')], [...routes()]);
    await screen.findByText('Staff Person');
    expect(screen.queryByRole('form', { name: 'Create user' })).not.toBeInTheDocument();
    expect(screen.queryByRole('combobox')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Delete' })).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Rights' })).not.toBeInTheDocument();
  });

  it('creates a user with the lowest assignable role by default', async () => {
    let list = users;
    const { fetch } = renderScreen(<UsersPage />, [perm('master-users', 'VRWUD')], [
      ['GET', '/api/users', () => list], ['GET', '/api/roles/assignable', assignable],
      ['POST', '/api/users', (body: unknown) => {
        list = [...users, { id: 'u3', email: (body as OrgUser).email, fullName: 'New Person', role: 'User', createdAt: '', lastLoginAt: null, canManage: true }];
        return list[2];
      }],
    ]);
    const form = await screen.findByRole('form', { name: 'Create user' });
    await userEvent.type(within(form).getByLabelText('Full name'), 'New Person');
    await userEvent.type(within(form).getByLabelText('Email'), 'new@demo.com');
    await userEvent.type(within(form).getByLabelText('Password'), 'Passw0rd!');
    await userEvent.click(within(form).getByRole('button', { name: 'Create user' }));

    expect(await screen.findByText('New Person')).toBeInTheDocument();
    const post = fetch.mock.calls.find(([, i]) => i?.method === 'POST')!;
    expect(JSON.parse(String(post[1]!.body))).toEqual(
      { fullName: 'New Person', email: 'new@demo.com', password: 'Passw0rd!', role: 'User' });
    expect(within(form).getByLabelText('Full name')).toHaveValue('');
  });

  it('shows the create error next to the button', async () => {
    renderScreen(<UsersPage />, [perm('master-users', 'VRWUD')], [
      ...routes(), ['POST', '/api/users', jsonResponse({ title: 'Email already registered' }, 409)],
    ]);
    const form = await screen.findByRole('form', { name: 'Create user' });
    await userEvent.type(within(form).getByLabelText('Full name'), 'X');
    await userEvent.type(within(form).getByLabelText('Email'), 'admin@demo.com');
    await userEvent.type(within(form).getByLabelText('Password'), 'Passw0rd!');
    await userEvent.click(within(form).getByRole('button', { name: 'Create user' }));
    expect(await within(form).findByRole('alert')).toHaveTextContent('Email already registered');
  });

  it('changes a role and deletes a user', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    const { fetch } = renderScreen(<UsersPage />, [perm('master-users', 'VRWUD')], [
      ...routes(),
      ['PUT', '/api/users/u2/role', (b: unknown) => ({ ...users[1], role: (b as { role: string }).role })],
      ['DELETE', '/api/users/u2', undefined],
    ]);
    await userEvent.selectOptions(await screen.findByRole('combobox', { name: 'Role of Staff Person' }), 'Admin');
    await waitFor(() => expect(calls(fetch)).toContain('PUT /api/users/u2/role'));
    const put = fetch.mock.calls.find(([, i]) => i?.method === 'PUT')!;
    expect(JSON.parse(String(put[1]!.body))).toEqual({ role: 'Admin' });

    await userEvent.click(screen.getByRole('button', { name: 'Delete' }));
    await waitFor(() => expect(calls(fetch)).toContain('DELETE /api/users/u2'));
  });

  it('shows the error when an action is refused', async () => {
    renderScreen(<UsersPage />, [perm('master-users', 'VRWUD')], [
      ...routes(), ['PUT', '/api/users/u2/role', jsonResponse({ title: 'You cannot assign that role' }, 403)],
    ]);
    await userEvent.selectOptions(await screen.findByRole('combobox', { name: 'Role of Staff Person' }), 'Admin');
    expect(await screen.findByRole('alert')).toHaveTextContent('You cannot assign that role');
  });
});

const userRights = (over: Partial<UserRights> = {}): UserRights => ({
  userId: 'u2', fullName: 'Staff Person', email: 'staff@demo.com', role: 'User', canEdit: true,
  pages: [
    { pageId: 2, pageKey: 'customers', pageName: 'Customers', menu, effective: R('VRWU'), source: 'system', roleDefault: R('VRWU'), isOverridden: false },
    { pageId: 3, pageKey: 'products', pageName: 'Products', menu, effective: R('VRWUD'), source: 'user', roleDefault: R('VR'), isOverridden: true },
  ],
  ...over,
});

describe('Master › User Rights screen', () => {
  it('asks for a user first, then loads that user’s matrix from the URL', async () => {
    renderScreen(<UserRightsPage />, [perm('master-user-rights', 'VRU')],
      [['GET', '/api/users', users], ['GET', '/api/master/users/u2/rights', userRights()]], '/?user=u2');

    expect(await screen.findByRole('heading', { name: /Staff Person/ })).toBeInTheDocument();
    expect(screen.getByRole('combobox', { name: 'User' })).toHaveValue('u2');
    expect(screen.getByText('Default (role default)')).toBeInTheDocument(); // source note for non-overridden rows
    expect(screen.getAllByText('Overridden').length).toBeGreaterThan(0);
  });

  it('shows nothing but the picker when no user is selected', async () => {
    const { fetch } = renderScreen(<UserRightsPage />, [perm('master-user-rights', 'VRU')], [['GET', '/api/users', users]]);
    expect(await screen.findByRole('option', { name: 'Staff Person (User)' })).toBeInTheDocument();
    expect(calls(fetch).some((c) => c.includes('/rights'))).toBe(false);
  });

  it('explains why a SuperAdmin’s rights are read-only', async () => {
    renderScreen(<UserRightsPage />, [perm('master-user-rights', 'VRU')], [
      ['GET', '/api/users', users],
      ['GET', '/api/master/users/u1/rights', userRights({ userId: 'u1', fullName: 'Demo Admin', role: 'SuperAdmin', canEdit: false })],
    ], '/?user=u1');
    expect(await screen.findByText('SuperAdmin always has every right.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Save/ })).not.toBeInTheDocument();
  });

  it('is read-only without the update right even when the server allows editing', async () => {
    renderScreen(<UserRightsPage />, [perm('master-user-rights', 'VR')],
      [['GET', '/api/users', users], ['GET', '/api/master/users/u2/rights', userRights()]], '/?user=u2');
    await screen.findByRole('heading', { name: /Staff Person/ });
    expect(screen.getAllByRole('checkbox').every((c) => (c as HTMLInputElement).disabled)).toBe(true);
  });

  it('shows the load error', async () => {
    renderScreen(<UserRightsPage />, [perm('master-user-rights', 'VRU')], [
      ['GET', '/api/users', users],
      ['GET', '/api/master/users/u2/rights', jsonResponse({ title: 'User not found' }, 404)],
    ], '/?user=u2');
    expect(await screen.findByRole('alert')).toHaveTextContent('User not found');
  });
});

const roleRights = (roleId: number, role: string, canEdit = true): RoleRights => ({
  roleId, role, canEdit,
  pages: [
    { pageId: 2, pageKey: 'customers', pageName: 'Customers', menu, rights: R('VRWU'), isCustomized: false, systemDefault: R('VRWU') },
    { pageId: 3, pageKey: 'products', pageName: 'Products', menu, rights: R('VRW'), isCustomized: true, systemDefault: R('VR') },
  ],
});

describe('Master › Role Rights screen', () => {
  it('opens on the User role and switches roles', async () => {
    const { fetch } = renderScreen(<RoleRightsPage />, [perm('master-role-rights', 'VRU')], [
      ['GET', '/api/roles', allRoles],
      ['GET', '/api/master/roles/2/rights', roleRights(2, 'User')],
      ['GET', '/api/master/roles/1/rights', roleRights(1, 'Admin', false)],
    ]);
    expect(await screen.findByRole('combobox', { name: 'Role' })).toHaveValue('2');
    expect(await screen.findAllByText('Customised')).not.toHaveLength(0);

    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Role' }), '1');
    expect(await screen.findByText('Read-only: you can only change defaults of roles below your own.')).toBeInTheDocument();
    expect(calls(fetch)).toEqual(expect.arrayContaining(['GET /api/master/roles/2/rights', 'GET /api/master/roles/1/rights']));
  });

  it('saves a changed default', async () => {
    const { fetch } = renderScreen(<RoleRightsPage />, [perm('master-role-rights', 'VRU')], [
      ['GET', '/api/roles', allRoles],
      ['GET', '/api/master/roles/2/rights', roleRights(2, 'User')],
      ['PUT', '/api/master/roles/2/rights', roleRights(2, 'User')],
    ]);
    await screen.findAllByText('Customised');
    const row = screen.getAllByText('Customers').map((e) => e.closest('tr')).find(Boolean)!;
    await userEvent.click(within(row).getAllByRole('checkbox')[5]); // Delete
    await userEvent.click(screen.getByRole('button', { name: /Save/ }));
    await waitFor(() => expect(calls(fetch)).toContain('PUT /api/master/roles/2/rights'));
  });

  it('shows the error when roles cannot be loaded', async () => {
    renderScreen(<RoleRightsPage />, [perm('master-role-rights', 'VRU')],
      [['GET', '/api/roles', jsonResponse({ title: 'Forbidden' }, 403)]]);
    expect(await screen.findByRole('alert')).toHaveTextContent('Forbidden');
  });
});
