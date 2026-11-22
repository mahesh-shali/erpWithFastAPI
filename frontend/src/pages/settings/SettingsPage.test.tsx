import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route as RouterRoute, Routes } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { OrgUser } from '../../api/client';
import type { AccountInfo } from '../../api/settings';
import { calls, perm, renderScreen, type Route } from '../../test/utils';
import { SettingsPage } from './SettingsPage';

const account = (over: Partial<AccountInfo> = {}): AccountInfo => ({
  id: 'u1', email: 'admin@demo.com', fullName: 'Demo Admin', role: 'Admin', twoFactorEnabled: false, recoveryCodesLeft: 0, ...over,
});

const orgUser = (over: Partial<OrgUser>): OrgUser => ({
  id: 'x', email: 'x@demo.com', fullName: 'X', role: 'User', createdAt: '2026-01-01', lastLoginAt: null,
  canManage: true, twoFactorEnabled: false, ...over,
});

function renderSettings(route: string, routes: Route[], pages = [perm('settings-users', 'U')]) {
  return renderScreen(
    <Routes><RouterRoute path="/settings/:tab?" element={<SettingsPage />} /></Routes>,
    pages, routes, route);
}

afterEach(() => vi.restoreAllMocks());

describe('SettingsPage', () => {
  it('shows the Users tab only with the settings-users right', async () => {
    renderSettings('/settings', [['GET', '/api/account', account()]], []);
    expect(await screen.findByRole('form', { name: 'Profile' })).toBeInTheDocument();
    const tabs = screen.getByRole('navigation', { name: /settings sections/i });
    expect(within(tabs).queryByRole('link', { name: 'Users' })).toBeNull();
    expect(within(tabs).queryByRole('link', { name: 'Pages' })).toBeNull();
    expect(within(tabs).getByRole('link', { name: 'Two-factor sign-in' })).toBeInTheDocument();
  });

  it('shows the Pages master as a tab for people who may read it', async () => {
    renderSettings('/settings/pages', [
      ['GET', '/api/account', account()],
      ['GET', '/api/master/pages', [{ id: 48, key: 'analytics', name: 'Analytics', route: '/analytics', isActive: true, isImplemented: true,
        menu: { moduleKey: null, module: null, moduleIcon: null, moduleSort: 0, groupKey: null, group: null, groupSort: 0, sortOrder: 15 } }]],
    ], [perm('master-pages', 'VR')]);
    const table = await screen.findByRole('table', { name: 'Pages' });
    expect(await within(table).findByText('analytics')).toBeInTheDocument();
    expect(screen.getAllByRole('heading', { level: 1 }).map((h) => h.textContent)).toEqual(['Settings']);
  });

  it('saves the profile and asks for the password only when the email changes', async () => {
    const { fetch } = renderSettings('/settings', [
      ['GET', '/api/account', account()],
      ['PUT', '/api/account/profile', (b: unknown) => account(b as Partial<AccountInfo>)],
    ]);
    const name = await screen.findByLabelText('Full name');
    expect(screen.queryByLabelText(/current password/i)).toBeNull();
    await userEvent.clear(name);
    await userEvent.type(name, 'Renamed Admin');
    const email = screen.getByLabelText('Email');
    await userEvent.clear(email);
    await userEvent.type(email, 'new@demo.com');
    await userEvent.type(screen.getByLabelText(/current password/i), 'Admin@123');
    await userEvent.click(screen.getByRole('button', { name: 'Save profile' }));

    expect(await screen.findByRole('status')).toHaveTextContent('Profile saved.');
    const put = fetch.mock.calls.find(([u, i]) => String(u) === '/api/account/profile' && i?.method === 'PUT')!;
    expect(JSON.parse(String(put[1]!.body))).toEqual({ fullName: 'Renamed Admin', email: 'new@demo.com', currentPassword: 'Admin@123' });
  });

  it('refuses mismatched new passwords without calling the API', async () => {
    const { fetch } = renderSettings('/settings/password', [['GET', '/api/account', account()]]);
    await userEvent.type(await screen.findByLabelText('Current password'), 'old-pass1');
    await userEvent.type(screen.getByLabelText('New password'), 'NewPass#123');
    await userEvent.type(screen.getByLabelText('Confirm new password'), 'NewPass#124');
    await userEvent.click(screen.getByRole('button', { name: 'Change password' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('do not match');
    expect(calls(fetch)).not.toContain('POST /api/account/password');
  });

  it('sets up two-factor: QR code + key, confirm with a code, then shows the recovery codes', async () => {
    let enabled = false;
    const { fetch } = renderSettings('/settings/security', [
      ['GET', '/api/account', () => account({ twoFactorEnabled: enabled, recoveryCodesLeft: enabled ? 10 : 0 })],
      ['POST', '/api/account/two-factor/setup', { secret: 'JBSWY3DPEHPK3PXP', otpAuthUri: 'otpauth://totp/ERP:admin?secret=JBSWY3DPEHPK3PXP' }],
      ['POST', '/api/account/two-factor/enable', (b: unknown) => {
        if ((b as { code: string }).code !== '123456') return new Response(JSON.stringify({ title: 'That code is not valid.' }), { status: 400 });
        enabled = true;
        return { recoveryCodes: ['aaaa-bbbb', 'cccc-dddd'] };
      }],
    ]);

    await userEvent.click(await screen.findByRole('button', { name: /set up two-factor/i }));
    expect(await screen.findByRole('img', { name: /qr code/i })).toHaveAttribute('src', expect.stringMatching(/^data:image\/svg\+xml/));
    expect(screen.getByLabelText('Setup key')).toHaveTextContent('JBSW Y3DP EHPK 3PXP');

    await userEvent.type(screen.getByLabelText('Code from the app'), '111111');
    await userEvent.click(screen.getByRole('button', { name: 'Turn on' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('That code is not valid.');

    await userEvent.type(screen.getByLabelText('Code from the app'), '123456');
    await userEvent.click(screen.getByRole('button', { name: 'Turn on' }));
    const codes = await screen.findByLabelText('Recovery codes');
    expect(within(codes).getByText('aaaa-bbbb')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'I saved them' }));
    expect(await screen.findByText('On')).toBeInTheDocument();
    expect(calls(fetch)).toContain('POST /api/account/two-factor/enable');
  });

  it('turns two-factor off with password and code', async () => {
    let enabled = true;
    const { fetch } = renderSettings('/settings/security', [
      ['GET', '/api/account', () => account({ twoFactorEnabled: enabled, recoveryCodesLeft: 2 })],
      ['POST', '/api/account/two-factor/disable', () => { enabled = false; return undefined; }],
    ]);
    expect(await screen.findByText(/running low/i)).toBeInTheDocument();
    const off = screen.getByRole('form', { name: 'Turn off' });
    await userEvent.type(within(off).getByLabelText('Password'), 'Admin@123');
    await userEvent.type(within(off).getByLabelText(/authenticator or recovery code/i), '654321');
    await userEvent.click(within(off).getByRole('button', { name: /turn off two-factor/i }));

    expect(await screen.findByRole('button', { name: /set up two-factor/i })).toBeInTheDocument();
    const post = fetch.mock.calls.find(([u]) => String(u) === '/api/account/two-factor/disable')!;
    expect(JSON.parse(String(post[1]!.body))).toEqual({ password: 'Admin@123', code: '654321' });
  });

  it('lists only editable users and saves a role change and new password', async () => {
    const { fetch } = renderSettings('/settings/users', [
      ['GET', '/api/account', account()],
      ['GET', '/api/settings/users/roles', [{ id: 4, name: 'Manager', description: null, level: 30 }, { id: 2, name: 'User', description: null, level: 10 }]],
      ['GET', '/api/settings/users', [
        orgUser({ id: 'owner', fullName: 'Owner', role: 'SuperAdmin', canManage: false }),
        orgUser({ id: 'u2', fullName: 'Staff One', email: 'staff@demo.com', role: 'User', twoFactorEnabled: true }),
      ]],
      ['PUT', '/api/settings/users/u2', (b: unknown) => orgUser({ id: 'u2', ...(b as object) })],
    ]);

    const table = await screen.findByRole('table', { name: 'Editable users' });
    expect(await within(table).findByText('Staff One')).toBeInTheDocument();
    expect(within(table).queryByText('Owner')).toBeNull();

    await userEvent.click(within(table).getByRole('button', { name: 'Edit' }));
    await userEvent.selectOptions(screen.getByLabelText('Role'), 'Manager');
    await userEvent.type(screen.getByLabelText(/new password/i), 'Reset#Pass1');
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }));

    expect(await screen.findByRole('status')).toHaveTextContent('signed out');
    const put = fetch.mock.calls.find(([u, i]) => String(u) === '/api/settings/users/u2' && i?.method === 'PUT')!;
    expect(JSON.parse(String(put[1]!.body))).toEqual({
      fullName: 'Staff One', email: 'staff@demo.com', role: 'Manager', newPassword: 'Reset#Pass1',
    });
  });

  it('resets two-factor for a user after confirmation', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    const { fetch } = renderSettings('/settings/users', [
      ['GET', '/api/account', account()],
      ['GET', '/api/settings/users/roles', [{ id: 2, name: 'User', description: null, level: 10 }]],
      ['GET', '/api/settings/users', [orgUser({ id: 'u2', fullName: 'Staff One', twoFactorEnabled: true })]],
      ['DELETE', '/api/settings/users/u2/two-factor', orgUser({ id: 'u2', twoFactorEnabled: false })],
    ]);
    await userEvent.click(await screen.findByRole('button', { name: 'Reset 2FA' }));
    await waitFor(() => expect(calls(fetch)).toContain('DELETE /api/settings/users/u2/two-factor'));
    expect(await screen.findByRole('status')).toHaveTextContent('turned off for Staff One');
  });

  it('sends users without the right away from the Users tab', async () => {
    renderSettings('/settings/users', [['GET', '/api/account', account()]], []);
    expect(await screen.findByRole('form', { name: 'Profile' })).toBeInTheDocument();
  });
});
