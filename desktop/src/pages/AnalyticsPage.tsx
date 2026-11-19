import { useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent, type PointerEvent } from 'react';
import {
  api, type AnalyticsPoint, type AnalyticsRange, type AnalyticsRanked, type AnalyticsReport, type AnalyticsStatus,
} from '../api/client';
import { Link } from 'react-router-dom';

const inr = new Intl.NumberFormat('en-IN', { style: 'currency', currency: 'INR', maximumFractionDigits: 0 });
const inrCompact = new Intl.NumberFormat('en-IN', {
  style: 'currency', currency: 'INR', notation: 'compact', maximumFractionDigits: 1,
});
const num = new Intl.NumberFormat('en-IN');
const pct = new Intl.NumberFormat('en-IN', { style: 'percent', maximumFractionDigits: 1, signDisplay: 'exceptZero' });

const RANGES: { days: AnalyticsRange; label: string; period: string }[] = [
  { days: 30, label: '30 days', period: 'previous 30 days' },
  { days: 90, label: '90 days', period: 'previous 90 days' },
  { days: 365, label: '12 months', period: 'previous 12 months' },
];

/** "2026-11-16" -> local Date (no timezone shift). */
const toDate = (iso: string) => {
  const [y, m, d] = iso.slice(0, 10).split('-').map(Number);
  return new Date(y, m - 1, d);
};
const dayFmt = new Intl.DateTimeFormat('en-IN', { day: 'numeric', month: 'short' });
const monthFmt = new Intl.DateTimeFormat('en-IN', { month: 'short', year: '2-digit' });
const longDayFmt = new Intl.DateTimeFormat('en-IN', { day: 'numeric', month: 'short', year: 'numeric' });
const longMonthFmt = new Intl.DateTimeFormat('en-IN', { month: 'long', year: 'numeric' });

export function AnalyticsPage() {
  return <AnalyticsPanel variant="page" />;
}

/**
 * The analytics view. "page" is the full Analytics screen; "dashboard" is the shorter section on the
 * Dashboard (KPIs, trend, status, top products and customers) with a link to the full page.
 * Render the dashboard variant only for users with read on the Analytics page: the API checks the same right.
 */
export function AnalyticsPanel({ variant }: { variant: 'page' | 'dashboard' }) {
  const full = variant === 'page';
  const [days, setDays] = useState<AnalyticsRange>(30);
  const [report, setReport] = useState<AnalyticsReport | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let alive = true;
    setLoading(true);
    setError(null);
    api.analytics(days)
      .then((r) => alive && setReport(r))
      .catch((e: Error) => alive && setError(e.message))
      .finally(() => alive && setLoading(false));
    return () => { alive = false; };
  }, [days]);

  const range = RANGES.find((r) => r.days === days)!;

  return (
    <div className={`analytics${loading && report ? ' is-loading' : ''}`} aria-busy={loading}
         aria-label={full ? undefined : 'Sales analytics'} role={full ? undefined : 'region'}>
      <div className="analytics-head">
        {full ? <h1>Analytics</h1> : (
          <div className="analytics-title">
            <h2>Sales analytics</h2>
            <Link to="/analytics">Open full analytics →</Link>
          </div>
        )}
        <div className="range" role="group" aria-label="Date range">
          {RANGES.map((r) => (
            <button key={r.days} type="button" aria-pressed={r.days === days} onClick={() => setDays(r.days)}>
              {r.label}
            </button>
          ))}
        </div>
      </div>
      <p className="hint">Revenue excludes cancelled orders. Figures are cached for up to a minute.</p>
      {error && <p role="alert" className="error">{error}</p>}

      {!report ? (!error && <p className="hint">Loading…</p>) : (
        <>
          <section className="stats" aria-label="Key figures">
            <Kpi label="Revenue" value={inr.format(report.current.revenue)}
                 now={report.current.revenue} before={report.previous.revenue} period={range.period} />
            <Kpi label="Orders" value={num.format(report.current.orders)}
                 now={report.current.orders} before={report.previous.orders} period={range.period} />
            <Kpi label="Average order value" value={inr.format(report.current.averageOrderValue)}
                 now={report.current.averageOrderValue} before={report.previous.averageOrderValue} period={range.period} />
            <Kpi label="New customers" value={num.format(report.current.newCustomers)}
                 now={report.current.newCustomers} before={report.previous.newCustomers} period={range.period} />
          </section>

          <section className="card">
            <h2>Revenue {report.bucket === 'month' ? 'by month' : 'by day'}</h2>
            <TrendChart points={report.trend} bucket={report.bucket} />
          </section>

          <section className="card">
            <h2>Orders by status</h2>
            <StatusBar statuses={report.statuses} />
          </section>

          <div className="analytics-grid">
            <RankedBars title="Top products" rows={report.topProducts} countLabel="units" />
            <RankedBars title="Top customers" rows={report.topCustomers} countLabel="orders" />
            {full && <RankedBars title="Revenue by city" rows={report.cities} countLabel="orders" />}
            {full && <RankedBars title="Revenue by category" rows={report.categories} countLabel="units" />}
          </div>
        </>
      )}
    </div>
  );
}

// ---------------------------------------------------------------- KPI tile
function Kpi({ label, value, now, before, period }:
  { label: string; value: string; now: number; before: number; period: string }) {
  const change = before > 0 ? (now - before) / before : null;
  const dir = change === null || change === 0 ? 'flat' : change > 0 ? 'up' : 'down';
  return (
    <div className="card stat">
      <span>{label}</span>
      <strong>{value}</strong>
      <small className={`delta delta-${dir}`}>
        {change === null ? `No data for the ${period}` : (
          <>
            <span aria-hidden="true">{dir === 'up' ? '▲' : dir === 'down' ? '▼' : '■'}</span>{' '}
            {pct.format(change)} vs {period}
          </>
        )}
      </small>
    </div>
  );
}

// ---------------------------------------------------------------- Revenue trend (single series, line + area)
const H = 260;
const M = { top: 12, right: 16, bottom: 28, left: 60 };

/** Clean axis ticks from 0: the step is 1 / 2 / 2.5 / 5 × 10^n, about four steps up to at least `max`. */
export function niceTicks(max: number): number[] {
  if (max <= 0) return [0, 1];
  const raw = max / 4;
  const exp = 10 ** Math.floor(Math.log10(raw));
  const f = raw / exp;
  const step = (f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10) * exp;
  const top = Math.ceil(max / step - 1e-9);
  return Array.from({ length: top + 1 }, (_, i) => i * step);
}

function useWidth<T extends HTMLElement>(fallback: number) {
  const ref = useRef<T>(null);
  const [width, setWidth] = useState(fallback);
  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    if (el.clientWidth) setWidth(el.clientWidth);
    if (typeof ResizeObserver === 'undefined') return;
    const ro = new ResizeObserver(([e]) => setWidth(Math.max(280, Math.round(e.contentRect.width))));
    ro.observe(el);
    return () => ro.disconnect();
  }, []);
  return [ref, width] as const;
}

function TrendChart({ points, bucket }: { points: AnalyticsPoint[]; bucket: 'day' | 'month' }) {
  const [wrapRef, width] = useWidth<HTMLDivElement>(720);
  const [active, setActive] = useState<number | null>(null);

  const n = points.length;
  const ticks = niceTicks(Math.max(0, ...points.map((p) => p.revenue)));
  const max = ticks[ticks.length - 1];
  const innerW = width - M.left - M.right;
  const innerH = H - M.top - M.bottom;
  const step = n > 1 ? innerW / (n - 1) : 0;
  const x = (i: number) => M.left + (n > 1 ? i * step : innerW / 2);
  const y = (v: number) => M.top + innerH * (1 - v / max);
  const short = (p: AnalyticsPoint) => (bucket === 'month' ? monthFmt : dayFmt).format(toDate(p.date));
  const long = (p: AnalyticsPoint) => (bucket === 'month' ? longMonthFmt : longDayFmt).format(toDate(p.date));

  // ~6 x labels whatever the width, always including the last point.
  const every = Math.max(1, Math.ceil(n / Math.max(2, Math.floor(innerW / 90))));
  const xLabels = points.map((_, i) => i).filter((i) => (n - 1 - i) % every === 0);

  const line = points.map((p, i) => `${i ? 'L' : 'M'}${x(i).toFixed(1)},${y(p.revenue).toFixed(1)}`).join('');
  const area = n ? `${line}L${x(n - 1).toFixed(1)},${y(0)}L${x(0).toFixed(1)},${y(0)}Z` : '';
  const total = points.reduce((s, p) => s + p.revenue, 0);
  const peak = points.reduce((b, p, i) => (p.revenue > points[b].revenue ? i : b), 0);

  const onMove = (e: PointerEvent<SVGRectElement>) => {
    const box = e.currentTarget.getBoundingClientRect();
    const i = Math.round((e.clientX - box.left) / (step || 1));
    setActive(Math.min(n - 1, Math.max(0, i)));
  };
  const onKey = (e: KeyboardEvent<SVGSVGElement>) => {
    if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return;
    e.preventDefault();
    setActive((a) => Math.min(n - 1, Math.max(0, (a ?? n - 1) + (e.key === 'ArrowRight' ? 1 : -1))));
  };

  const a = active === null ? null : points[active];
  const tipLeft = active === null ? 0 : x(active);

  return (
    <>
      <div className="trend" ref={wrapRef}>
        <svg width={width} height={H} role="img" tabIndex={0} onKeyDown={onKey} onBlur={() => setActive(null)}
             aria-label={`Revenue ${bucket === 'month' ? 'per month' : 'per day'}: ${inr.format(total)} in total, `
               + `highest ${n ? `${inr.format(points[peak].revenue)} on ${long(points[peak])}` : 'none'}. `
               + 'Use the left and right arrow keys to read each point.'}>
          {ticks.map((t) => (
            <g key={t}>
              <line className={t === 0 ? 'axis' : 'grid'} x1={M.left} x2={width - M.right} y1={y(t)} y2={y(t)} />
              <text className="tick" x={M.left - 8} y={y(t)} dy="0.32em" textAnchor="end">{inrCompact.format(t)}</text>
            </g>
          ))}
          {xLabels.map((i) => (
            <text key={i} className="tick" x={x(i)} y={H - 8}
                  textAnchor={i === 0 ? 'start' : i === n - 1 ? 'end' : 'middle'}>{short(points[i])}</text>
          ))}
          <path className="area" d={area} />
          <path className="line" d={line} />
          {a && active !== null && (
            <g>
              <line className="crosshair" x1={tipLeft} x2={tipLeft} y1={M.top} y2={M.top + innerH} />
              <circle className="marker" cx={tipLeft} cy={y(a.revenue)} r={5} />
            </g>
          )}
          <rect className="hit" x={M.left - step / 2} y={M.top} width={innerW + step} height={innerH}
                onPointerMove={onMove} onPointerLeave={() => setActive(null)} />
        </svg>
        {a && (
          <div className="tooltip" role="status"
               style={{ left: tipLeft, top: y(a.revenue), transform: `translate(${tipLeft > width * 0.7 ? 'calc(-100% - 12px)' : '12px'}, -50%)` }}>
            <b>{long(a)}</b>
            <span>{inr.format(a.revenue)}</span>
            <span>{num.format(a.orders)} {a.orders === 1 ? 'order' : 'orders'}</span>
          </div>
        )}
      </div>
      {total === 0 && <p className="hint">No orders in this period.</p>}
      <details className="data-table">
        <summary>Show data table</summary>
        <div className="table-wrap">
          <table>
            <thead><tr><th>{bucket === 'month' ? 'Month' : 'Day'}</th><th className="num">Revenue</th><th className="num">Orders</th></tr></thead>
            <tbody>
              {points.map((p) => (
                <tr key={p.date}><td>{long(p)}</td><td className="num">{inr.format(p.revenue)}</td><td className="num">{num.format(p.orders)}</td></tr>
              ))}
            </tbody>
          </table>
        </div>
      </details>
    </>
  );
}

// ---------------------------------------------------------------- Order status (tall segmented bar)
function StatusBar({ statuses }: { statuses: AnalyticsStatus[] }) {
  const total = statuses.reduce((s, x) => s + x.orders, 0);
  if (total === 0) return <p className="hint">No orders in this period.</p>;
  const share = (x: AnalyticsStatus) => x.orders / total;
  return (
    <>
      <div className="segbar" role="list" aria-label={`${num.format(total)} orders by status`}>
        {statuses.map((s, i) => s.orders > 0 && (
          <div key={s.status} role="listitem" tabIndex={0} className={`seg s${i + 1}`} style={{ flexGrow: s.orders }}
               title={`${s.status}: ${num.format(s.orders)} orders (${pct.format(share(s)).replace('+', '')}), ${inr.format(s.amount)}`}
               aria-label={`${s.status}: ${num.format(s.orders)} orders, ${pct.format(share(s)).replace('+', '')}, ${inr.format(s.amount)}`}>
            {share(s) >= 0.08 && <span aria-hidden="true">{pct.format(share(s)).replace('+', '')}</span>}
          </div>
        ))}
      </div>
      <ul className="legend">
        {statuses.map((s, i) => (
          <li key={s.status}>
            <i className={`swatch s${i + 1}`} aria-hidden="true" />
            <b>{s.status}</b> {num.format(s.orders)} orders · {inr.format(s.amount)}
          </li>
        ))}
      </ul>
    </>
  );
}

// ---------------------------------------------------------------- Ranked horizontal bars (single series)
function RankedBars({ title, rows, countLabel }: { title: string; rows: AnalyticsRanked[]; countLabel: 'units' | 'orders' }) {
  const unit = (n: number) => `${num.format(n)} ${n === 1 ? countLabel.slice(0, -1) : countLabel}`;
  const max = Math.max(0, ...rows.map((r) => r.revenue));
  return (
    <section className="card ranked">
      <h2>{title}</h2>
      {rows.length === 0 ? <p className="hint">No sales in this period.</p> : (
        <ol>
          {rows.map((r) => (
            <li key={r.name} title={`${r.name}: ${inr.format(r.revenue)}, ${unit(r.count)}`}>
              <div className="ranked-label">
                <span className="ranked-name">{r.name}</span>
                <span className="ranked-count">{unit(r.count)}</span>
              </div>
              <div className="ranked-track">
                <div className="ranked-bar" style={{ width: `calc((100% - 5.5rem) * ${max ? r.revenue / max : 0})` }} />
                <span className="ranked-value">{inrCompact.format(r.revenue)}</span>
              </div>
            </li>
          ))}
        </ol>
      )}
    </section>
  );
}
