import { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { api, ITEM_TYPES, type PagedResult, type StockDetailsRow, type StockItemDetails, type StockStatus } from '../../api/client';
import { usePermissions } from '../../auth/PermissionsContext';
import { PO_ROUTE, Pager, WO_ROUTE } from '../sales/shared';
import { ENTRY_ROUTE } from './StockEntryPages';
import { errorText, fmtDate, inr, num, rate, signed } from './format';

export const DETAILS_ROUTE = '/inventory/stock-details';

const STATUS_TEXT: Record<StockStatus, string> = { OK: 'OK', Reorder: 'Reorder', Short: 'Short' };
function StockBadge({ status }: { status: StockStatus }) {
  return <span className={`status stock-${status.toLowerCase()}`}>{STATUS_TEXT[status]}</span>;
}

/** Link to the document behind a stock movement, when there is a screen for it. */
function VoucherLink({ type, id, no }: { type: string; id: string | null; no: string }) {
  if (!id) return <>{no}</>;
  const to = type === 'Stock Entry' ? `${ENTRY_ROUTE}/${id}` : type === 'Work Order' ? `${WO_ROUTE}/${id}`
    : type === 'Sales Invoice' ? `/sales/invoices/${id}` : null;
  return to ? <Link to={to}>{no}</Link> : <>{no}</>;
}

/**
 * Inventory › Stock Position › Stock Details: per item, what is on hand, what confirmed customer P.O.s still need
 * (committed), what is planned on draft work orders, and what is really available.
 */
export function StockDetailsPage() {
  const [f, setF] = useState({ search: '', itemType: '', attention: false, page: 1 });
  const [search, setSearch] = useState('');
  const [data, setData] = useState<PagedResult<StockDetailsRow> | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => { const t = setTimeout(() => setF((x) => (x.search === search ? x : { ...x, search, page: 1 })), 300); return () => clearTimeout(t); }, [search]);
  useEffect(() => {
    let alive = true;
    api.stockDetails(f).then((d) => alive && setData(d)).catch((e) => alive && setError(errorText(e)));
    return () => { alive = false; };
  }, [f]);

  return (
    <>
      <h1>Stock details</h1>
      {error && <p role="alert" className="error">{error}</p>}
      <div className="card toolbar">
        <label className="grow">Search<input type="search" placeholder="Item name or SKU" value={search} onChange={(e) => setSearch(e.target.value)} /></label>
        <label>Item type
          <select aria-label="Item type" value={f.itemType} onChange={(e) => setF({ ...f, itemType: e.target.value, page: 1 })}>
            <option value="">All types</option>{ITEM_TYPES.map((t) => <option key={t}>{t}</option>)}
          </select>
        </label>
        <label className="check-label"><input type="checkbox" checked={f.attention} onChange={(e) => setF({ ...f, attention: e.target.checked, page: 1 })} /> Needs attention only</label>
      </div>
      <div className="card table-wrap">
        <table aria-label="Stock details">
          <thead>
            <tr><th>SKU</th><th>Item</th><th>Type</th><th>Unit</th><th className="num">On hand</th><th className="num">Committed</th>
              <th className="num">Available</th><th className="num">In production</th><th className="num">Reorder at</th><th className="num">Value</th><th>Status</th></tr>
          </thead>
          <tbody>
            {data?.items.map((r) => (
              <tr key={r.productId}>
                <td>{r.sku}</td><td><Link to={`${DETAILS_ROUTE}/${r.productId}`}>{r.name}</Link></td><td>{r.itemType}</td><td>{r.uom ?? '—'}</td>
                <td className="num">{num.format(r.onHand)}</td><td className="num">{num.format(r.committed)}</td>
                <td className={`num ${r.available < 0 ? 'out' : ''}`}>{num.format(r.available)}</td>
                <td className="num">{num.format(r.inProduction)}</td><td className="num">{num.format(r.reorderLevel)}</td>
                <td className="num">{inr.format(r.stockValue)}</td><td><StockBadge status={r.status} /></td>
              </tr>
            ))}
          </tbody>
        </table>
        {data && data.items.length === 0 && <p className="hint">No items for these filters.</p>}
        <p className="hint">Committed = still to be invoiced on confirmed customer P.O.s. Available = on hand − committed.</p>
        {data && <Pager page={f.page} total={data.total} pageSize={data.pageSize} onPage={(page) => setF({ ...f, page })} />}
      </div>
    </>
  );
}

/** /inventory/stock-details/:productId — one item: master details, stores, commitments, planned production, movements. */
export function StockItemDetailsPage() {
  const { productId } = useParams();
  const { can } = usePermissions();
  const [d, setD] = useState<StockItemDetails | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { api.stockItemDetails(productId!).then(setD).catch((e) => setError(errorText(e))); }, [productId]);

  if (error) return <><p className="crumbs"><Link to={DETAILS_ROUTE}>Stock details</Link></p><p role="alert" className="error">{error}</p></>;
  if (!d) return <p className="hint">Loading…</p>;
  const { item, summary: s } = d;
  return (
    <>
      <p className="crumbs"><Link to={DETAILS_ROUTE}>Stock details</Link> › {item.sku}</p>
      <div className="page-head">
        <h1>{item.name} <StockBadge status={s.status} /></h1>
        <div className="row-actions">
          {can('stock-report', 'read') && <Link to={`/reports/stock?productId=${item.id}`}>Full ledger</Link>}
          {can('work-orders', 'write') && <Link className="button" to={`${WO_ROUTE}/new`}>New work order</Link>}
        </div>
      </div>
      <section className="stats" aria-label="Stock summary">
        <div className="card stat"><span>On hand</span><strong>{num.format(s.onHand)}</strong></div>
        <div className="card stat"><span>Committed to P.O.s</span><strong>{num.format(s.committed)}</strong></div>
        <div className="card stat"><span>Available</span><strong className={s.available < 0 ? 'out' : undefined}>{num.format(s.available)}</strong></div>
        <div className="card stat"><span>In production</span><strong>{num.format(s.inProduction)}</strong></div>
        <div className="card stat"><span>Stock value</span><strong>{inr.format(s.stockValue)}</strong></div>
      </section>

      <section className="card" aria-label="Item details">
        <h2>Item</h2>
        <dl className="facts">
          <dt>SKU</dt><dd>{item.sku}</dd>
          <dt>Type</dt><dd>{item.itemType}</dd>
          <dt>Category</dt><dd>{item.category ?? '—'}</dd>
          <dt>Unit</dt><dd>{item.uom ?? '—'}</dd>
          <dt>HSN / SAC</dt><dd>{item.hsnCode ?? '—'}</dd>
          <dt>GST</dt><dd>{item.taxRate ?? 0}%</dd>
          <dt>Sales price</dt><dd>{inr.format(item.unitPrice)}</dd>
          <dt>Standard cost</dt><dd>{inr.format(item.standardCost ?? 0)}</dd>
          <dt>Reorder level</dt><dd>{num.format(item.reorderLevel ?? 0)}</dd>
          <dt>Lead time</dt><dd>{item.leadTimeDays ?? 0} days</dd>
          <dt>Drawing / part no.</dt><dd>{item.drawingNo ?? '—'}</dd>
          <dt>Status</dt><dd>{item.isActive === false ? 'Inactive' : 'Active'}</dd>
        </dl>
        {item.description && <p>{item.description}</p>}
      </section>

      <div className="card table-wrap">
        <h2>Stock by store</h2>
        <table aria-label="Stock by store">
          <thead><tr><th>Store</th><th className="num">Quantity</th><th className="num">Valuation rate</th><th className="num">Value</th></tr></thead>
          <tbody>
            {d.stores.map((b) => (
              <tr key={b.storeId}><td>{b.store}</td><td className="num">{num.format(b.quantity)}</td>
                <td className="num">{rate.format(b.valuationRate)}</td><td className="num">{inr.format(b.stockValue)}</td></tr>
            ))}
          </tbody>
        </table>
        {d.stores.length === 0 && <p className="hint">Not in stock in any store.</p>}
      </div>

      <div className="card table-wrap">
        <h2>Committed to customer P.O.s</h2>
        <table aria-label="Commitments">
          <thead><tr><th>P.O.</th><th>Customer</th><th>Delivery</th><th className="num">Ordered</th><th className="num">Produced</th>
            <th className="num">Invoiced</th><th className="num">Pending</th></tr></thead>
          <tbody>
            {d.commitments.map((c) => (
              <tr key={c.customerPoId}>
                <td><Link to={`${PO_ROUTE}/${c.customerPoId}`}>{c.number}</Link></td><td>{c.customerName}</td>
                <td>{c.deliveryDate ? fmtDate(c.deliveryDate) : '—'}</td><td className="num">{num.format(c.ordered)}</td>
                <td className="num">{num.format(c.produced)}</td><td className="num">{num.format(c.invoiced)}</td><td className="num">{num.format(c.pending)}</td>
              </tr>
            ))}
          </tbody>
        </table>
        {d.commitments.length === 0 && <p className="hint">No open customer P.O.s for this item.</p>}
        {d.openWorkOrders.length > 0 && (
          <p className="hint">Planned production: {d.openWorkOrders.map((w, i) => (
            <span key={w.id}>{i > 0 && ', '}<Link to={`${WO_ROUTE}/${w.id}`}>{w.number}</Link> × {num.format(w.amount)}{w.detail && ` (${w.detail})`}</span>
          ))}</p>
        )}
      </div>

      <div className="card table-wrap">
        <h2>Recent movements</h2>
        <table aria-label="Recent movements">
          <thead><tr><th>Date</th><th>Store</th><th>Voucher</th><th className="num">In / out</th><th className="num">Balance</th><th className="num">Rate</th></tr></thead>
          <tbody>
            {d.recentMovements.map((m) => (
              <tr key={m.id} className={m.isReversal ? 'reversal' : undefined}>
                <td>{fmtDate(m.postingDate)}</td><td>{m.store}</td>
                <td>{m.voucherType} <VoucherLink type={m.voucherType} id={m.voucherId} no={m.voucherNo} />{m.isReversal && <span className="tag">Reversal</span>}</td>
                <td className={`num ${m.qtyChange < 0 ? 'out' : 'in'}`}>{signed(m.qtyChange)}</td>
                <td className="num">{num.format(m.qtyAfter)}</td><td className="num">{rate.format(m.rate)}</td>
              </tr>
            ))}
          </tbody>
        </table>
        {d.recentMovements.length === 0 && <p className="hint">No stock movements yet.</p>}
      </div>
    </>
  );
}
