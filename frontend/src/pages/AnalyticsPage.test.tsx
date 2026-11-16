import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import type { AnalyticsReport } from '../api/client';
import { jsonResponse, renderWithProviders, signIn } from '../test/utils';
import { AnalyticsPage, niceTicks } from './AnalyticsPage';

const report = (over: Partial<AnalyticsReport> = {}): AnalyticsReport => ({
  days: 30, bucket: 'day', from: '2026-10-18T00:00:00Z', to: '2026-11-16T12:00:00Z',
  current: { revenue: 120000, orders: 40, averageOrderValue: 3000, newCustomers: 5 },
  previous: { revenue: 100000, orders: 50, averageOrderValue: 2000, newCustomers: 0 },
  trend: [
    { date: '2026-11-14', revenue: 20000, orders: 6 },
    { date: '2026-11-15', revenue: 0, orders: 0 },
    { date: '2026-11-16', revenue: 100000, orders: 34 },
  ],
  statuses: [
    { status: 'Pending', orders: 10, amount: 30000 },
    { status: 'Paid', orders: 20, amount: 60000 },
    { status: 'Shipped', orders: 10, amount: 30000 },
    { status: 'Cancelled', orders: 0, amount: 0 },
  ],
  topProducts: [{ name: 'Widget', revenue: 50000, count: 12 }, { name: 'Gadget', revenue: 25000, count: 3 }],
  topCustomers: [{ name: 'Acme', revenue: 70000, count: 4 }],
  cities: [{ name: 'Pune', revenue: 120000, count: 40 }],
  categories: [],
  ...over,
});

describe('AnalyticsPage', () => {
  it('shows KPIs with change vs the previous period', async () => {
    signIn();
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(jsonResponse(report()));
    renderWithProviders(<AnalyticsPage />);

    const kpis = await screen.findByRole('region', { name: 'Key figures' });
    expect(within(kpis).getByText('Revenue').parentElement).toHaveTextContent('+20% vs previous 30 days');
    expect(within(kpis).getByText('Orders').parentElement).toHaveTextContent('-20% vs previous 30 days');
    expect(within(kpis).getByText('New customers').parentElement).toHaveTextContent('No data for the previous 30 days');
  });

  it('renders the trend, status breakdown and ranked lists', async () => {
    signIn();
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(jsonResponse(report()));
    renderWithProviders(<AnalyticsPage />);

    expect(await screen.findByRole('img', { name: /Revenue per day/ })).toBeInTheDocument();
    // Zero-order statuses draw no segment but stay in the legend.
    const bar = screen.getByRole('list', { name: '40 orders by status' });
    expect(within(bar).getAllByRole('listitem')).toHaveLength(3);
    expect(within(bar).getByLabelText(/^Paid: 20 orders, 50%/)).toBeInTheDocument();
    expect(screen.getByText('Cancelled')).toBeInTheDocument();
    expect(screen.getByText('Widget')).toBeInTheDocument();
    expect(screen.getByText('12 units')).toBeInTheDocument();
    expect(screen.getByText('No sales in this period.')).toBeInTheDocument(); // empty categories
    // Table view of the trend
    expect(screen.getAllByRole('row')).toHaveLength(4);
  });

  it('reloads when the range changes', async () => {
    signIn();
    const f = vi.spyOn(globalThis, 'fetch')
      .mockResolvedValueOnce(jsonResponse(report()))
      .mockResolvedValueOnce(jsonResponse(report({ days: 365, bucket: 'month' })));
    renderWithProviders(<AnalyticsPage />);
    await screen.findByRole('img', { name: /per day/ });

    await userEvent.click(screen.getByRole('button', { name: '12 months' }));

    expect(await screen.findByRole('img', { name: /per month/ })).toBeInTheDocument();
    expect(f.mock.calls.map(([u]) => u)).toEqual(['/api/analytics?days=30', '/api/analytics?days=365']);
    expect(screen.getByRole('button', { name: '12 months' })).toHaveAttribute('aria-pressed', 'true');
  });

  it('shows the API error', async () => {
    signIn();
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(jsonResponse({ title: 'Forbidden' }, 403));
    renderWithProviders(<AnalyticsPage />);
    expect(await screen.findByRole('alert')).toHaveTextContent('Forbidden');
  });

  it('picks clean axis ticks', () => {
    expect(niceTicks(0)).toEqual([0, 1]);
    expect(niceTicks(87000)).toEqual([0, 25000, 50000, 75000, 100000]);
    expect(niceTicks(2_200_000)).toEqual([0, 1_000_000, 2_000_000, 3_000_000]);
    expect(niceTicks(160000)).toEqual([0, 50000, 100000, 150000, 200000]);
  });
});
