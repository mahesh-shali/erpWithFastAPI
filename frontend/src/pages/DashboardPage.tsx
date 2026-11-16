import { useEffect, useState } from 'react';
import { api, type DashboardStats } from '../api/client';
import { usePermissions } from '../auth/PermissionsContext';
import { AnalyticsPanel } from './AnalyticsPage';

const inr = new Intl.NumberFormat('en-IN', { style: 'currency', currency: 'INR', maximumFractionDigits: 0 });

export function DashboardPage() {
  const [stats, setStats] = useState<DashboardStats | null>(null);
  const [error, setError] = useState<string | null>(null);
  const { can } = usePermissions();

  useEffect(() => {
    let alive = true;
    api.stats().then((s) => alive && setStats(s)).catch((e: Error) => alive && setError(e.message));
    return () => { alive = false; };
  }, []);

  return (
    <>
      <h1>Dashboard</h1>
      {error && <p role="alert" className="error">{error}</p>}
      <section className="stats">
        <Stat label="Total revenue" value={stats ? inr.format(stats.revenue) : '…'} />
        <Stat label="Total orders" value={stats?.orders ?? '…'} />
        <Stat label="Customers" value={stats?.customers ?? '…'} />
        <Stat label="Products" value={stats?.products ?? '…'} />
        <Stat label="Low stock" value={stats?.lowStockProducts ?? '…'} />
      </section>
      {/* Only for users who may read the Analytics page; the API enforces the same right. */}
      {can('analytics') && <AnalyticsPanel variant="dashboard" />}
    </>
  );
}

function Stat({ label, value }: { label: string; value: string | number }) {
  return (
    <div className="card stat">
      <span>{label}</span>
      <strong>{value}</strong>
    </div>
  );
}
