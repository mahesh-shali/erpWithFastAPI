import { screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { AnalyticsReport, MyPagePermission } from '../api/client';
import { PermissionsProvider } from '../auth/PermissionsContext';
import { jsonResponse, renderWithProviders, signIn } from '../test/utils';
import { DashboardPage } from './DashboardPage';

const perm = (pageKey: string, canRead: boolean): MyPagePermission => ({
  pageKey, name: pageKey, route: '/', isImplemented: true,
  menu: { moduleKey: null, module: null, moduleIcon: null, moduleSort: 0, groupKey: null, group: null, groupSort: 0, sortOrder: 10 },
  isVisible: canRead, isRestricted: false, canRead, canWrite: false, canUpdate: false, canDelete: false,
});

const stats = { customers: 200, products: 100, orders: 1000, revenue: 5000000, lowStockProducts: 3 };
const report: AnalyticsReport = {
  days: 30, bucket: 'day', from: '2026-10-18T00:00:00Z', to: '2026-11-16T12:00:00Z',
  current: { revenue: 100, orders: 1, averageOrderValue: 100, newCustomers: 0 },
  previous: { revenue: 0, orders: 0, averageOrderValue: 0, newCustomers: 0 },
  trend: [{ date: '2026-11-16', revenue: 100, orders: 1 }],
  statuses: [{ status: 'Paid', orders: 1, amount: 100 }],
  topProducts: [{ name: 'Widget', revenue: 100, count: 1 }],
  topCustomers: [{ name: 'Acme', revenue: 100, count: 1 }],
  cities: [{ name: 'Pune', revenue: 100, count: 1 }],
  categories: [{ name: 'Tools', revenue: 100, count: 1 }],
};

function mockApi(canReadAnalytics: boolean) {
  return vi.spyOn(globalThis, 'fetch').mockImplementation(async (input) => {
    const url = String(input);
    if (url === '/api/me/permissions')
      return jsonResponse({ role: 'User', pages: [perm('dashboard', true), perm('analytics', canReadAnalytics)] });
    if (url === '/api/dashboard/stats') return jsonResponse(stats);
    if (url.startsWith('/api/analytics')) return jsonResponse(report);
    return jsonResponse({}, 404);
  });
}

const renderDashboard = () => renderWithProviders(<PermissionsProvider><DashboardPage /></PermissionsProvider>);

describe('DashboardPage', () => {
  it('shows the analytics section to users who can read Analytics', async () => {
    signIn();
    mockApi(true);
    renderDashboard();

    expect(await screen.findByRole('region', { name: 'Sales analytics' })).toBeInTheDocument();
    expect(await screen.findByText('Widget')).toBeInTheDocument();
    expect(screen.getByText('Acme')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /Open full analytics/ })).toHaveAttribute('href', '/analytics');
    // The shorter dashboard version leaves city and category out.
    expect(screen.queryByText('Revenue by city')).not.toBeInTheDocument();
  });

  it('hides it, and never calls the analytics API, without the Analytics right', async () => {
    signIn();
    const f = mockApi(false);
    renderDashboard();

    expect(await screen.findByText('1000')).toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'Sales analytics' })).not.toBeInTheDocument();
    expect(f.mock.calls.map(([u]) => String(u))).not.toContainEqual(expect.stringContaining('/api/analytics'));
  });
});
