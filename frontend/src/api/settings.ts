import { request, type OrgUser, type RoleDto } from './client';

/**
 * Settings screen API (same file in the web app and the desktop app).
 *  - /api/account: the caller's own profile, password and two-factor sign-in (every signed-in user);
 *  - /api/settings/users: edit people in lower roles (needs Update on "settings-users"; the API enforces the hierarchy).
 */
export interface AccountInfo {
  id: string; email: string; fullName: string; role: string; twoFactorEnabled: boolean; recoveryCodesLeft: number;
  /** Browsers/apps that skip the two-factor code for now ("Remember this device for 30 days"). */
  trustedDevices?: number;
}
export interface TwoFactorSetup { secret: string; otpAuthUri: string }
export interface RecoveryCodes { recoveryCodes: string[] }
/** role/newPassword null = keep the current one. */
export interface UpdateUser { fullName: string; email: string; role: string | null; newPassword: string | null }

const json = (body: unknown) => JSON.stringify(body);

export const settingsApi = {
  account: () => request<AccountInfo>('/api/account'),
  updateProfile: (p: { fullName: string; email: string; currentPassword: string | null }) =>
    request<AccountInfo>('/api/account/profile', { method: 'PUT', body: json(p) }),
  changePassword: (currentPassword: string, newPassword: string) =>
    request<void>('/api/account/password', { method: 'POST', body: json({ currentPassword, newPassword }) }),

  setupTwoFactor: () => request<TwoFactorSetup>('/api/account/two-factor/setup', { method: 'POST' }),
  enableTwoFactor: (code: string) =>
    request<RecoveryCodes>('/api/account/two-factor/enable', { method: 'POST', body: json({ code }) }),
  disableTwoFactor: (password: string, code: string) =>
    request<void>('/api/account/two-factor/disable', { method: 'POST', body: json({ password, code }) }),
  regenerateRecoveryCodes: (code: string) =>
    request<RecoveryCodes>('/api/account/two-factor/recovery-codes', { method: 'POST', body: json({ code }) }),
  /** Every remembered device has to enter a code again. */
  forgetTrustedDevices: () =>
    request<{ forgotten: number }>('/api/account/two-factor/trusted-devices', { method: 'DELETE' }),

  users: () => request<OrgUser[]>('/api/settings/users'),
  assignableRoles: () => request<RoleDto[]>('/api/settings/users/roles'),
  updateUser: (id: string, u: UpdateUser) =>
    request<OrgUser>(`/api/settings/users/${id}`, { method: 'PUT', body: json(u) }),
  resetTwoFactor: (id: string) => request<OrgUser>(`/api/settings/users/${id}/two-factor`, { method: 'DELETE' }),
};
