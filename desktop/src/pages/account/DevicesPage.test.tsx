import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { calls, renderScreen } from '../../test/utils';
import { DevicesPage, deviceLabel } from './DevicesPage';

const t = '2026-11-19T10:00:00Z';
const sessions = [
  { id: 's1', createdAt: t, lastSeenAt: t, expiresAt: t, ipAddress: '10.0.0.1', userAgent: 'ERP-Desktop/1.0.0 (win32) Electron/33', current: true },
  { id: 's2', createdAt: t, lastSeenAt: t, expiresAt: t, ipAddress: '10.0.0.2', userAgent: 'Mozilla/5.0 Chrome/131 Safari/537', current: false },
];

describe('DevicesPage', () => {
  it('lists sessions and signs out another device', async () => {
    const { fetch } = renderScreen(<DevicesPage />, [], [
      ['GET', '/api/auth/sessions', sessions],
      ['DELETE', '/api/auth/sessions/s2', undefined],
    ]);
    expect(await screen.findByText(/Desktop app 1.0.0 on Windows/)).toBeInTheDocument();
    expect(screen.getByText('This device')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Sign out' }));
    await waitFor(() => expect(calls(fetch)).toContain('DELETE /api/auth/sessions/s2'));
  });

  it('signs out all other devices', async () => {
    const { fetch } = renderScreen(<DevicesPage />, [], [
      ['GET', '/api/auth/sessions', sessions],
      ['POST', '/api/auth/logout-others', { revoked: 1 }],
    ]);
    await userEvent.click(await screen.findByRole('button', { name: 'Sign out all other devices' }));
    expect(await screen.findByText('Signed out 1 other session.')).toBeInTheDocument();
    expect(calls(fetch)).toContain('POST /api/auth/logout-others');
  });

  it('labels clients from their user agent', () => {
    expect(deviceLabel('ERP-Desktop/2.1.0 (darwin) Electron/33')).toBe('Desktop app 2.1.0 on macOS');
    expect(deviceLabel('Mozilla/5.0 Edg/131')).toBe('Edge browser');
    expect(deviceLabel(null)).toBe('Unknown device');
  });
});
