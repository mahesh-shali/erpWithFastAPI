import { useEffect, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { api, type ItemLookup, type PagedResult, type StockLedgerRow, type StockPosition, type Store } from '../../api/client';
import { ENTRY_ROUTE } from './StockEntryPages';
import { errorText, fmtDate, inr, num, rate, signed } from './format';

function useLookups() {
  const [stores, setStores] = useState<Store[]>([]);
  const [items, setItems] = useState<ItemLookup[]>([]);
  useEffect(() => {
    api.lookupStores().then(setStores).catch(() => undefined);
    api.lookupItems().then(setItems).catch(() => undefined);
  }, []);
  return { stores, items };
}

function Pager({ page, total, pageSize, onPage }: { page: number; total: number; pageSize: number; onPage: (p: number) => void }) {
  const pages = Math.max(1, Math.ceil(total / pageSize));
  if (pages <= 1) return null;
  return (
    <div className="pager">
      <button className="link" disabled={page <= 1} onClick={() => onPage(page - 1)}>‹ Previous</button>
      <span>Page {page} of {pages}</span>
      <button className="link" disabled={page >= pages} onClick={() => onPage(page + 1)}>Next ›</button>
    </div>
  );
}

/** Inventory › Stock Position: quantity and value per item and store. */
export function StockPositionPage() {
  const { stores } = useLookups();
  const [f, setF] = useState({ storeId: '', search: '', lowOnly: false, page: 1 });
  const [search, setSearch] = useState('');
  const [data, setData] = useState<StockPosition | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => { const t = setTimeout(() => setF((x) => ({ ...x, search, page: 1 })), 300); return () => clearTimeout(t); }, [search]);
  useEffect(() => {
    let alive = true;
    api.stockPosition(f).then((d) => alive && setData(d)).catch((e) => alive && setError(errorText(e)));
    return () => { alive = false; };
  }, [f]);

  return (
    <>
      <h1>Stock position</h1>
      {error && <p role="alert" className="error">{error}</p>}
      <div className="card toolbar">
        <label>Store
          <select aria-label="Store" value={f.storeId} onChange={(e) => setF({ ...f, storeId: e.target.value, page: 1 })}>
            <option value="">All stores</option>{stores.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
          </select>
        </label>
        <label className="grow">Search<input type="search" placeholder="Item name or SKU" value={search} onChange={(e) => setSearch(e.target.value)} /></label>
        <label className="check-label"><input type="checkbox" checked={f.lowOnly} onChange={(e) => setF({ ...f, lowOnly: e.target.checked, page: 1 })} /> Low stock only (below 10)</label>
      </div>
      {data && (
        <section className="stats" aria-label="Totals">
          <div className="card stat"><span>Lines</span><strong>{num.format(data.total)}</strong></div>
          <div className="card stat"><span>Quantity</span><strong>{num.format(data.totalQuantity)}</strong></div>
          <div className="card stat"><span>Stock value</span><strong>{inr.format(data.totalValue)}</strong></div>
        </section>
      )}
      <div className="card table-wrap">
        <table aria-label="Stock position">
          <thead>
            <tr><th>SKU</th><th>Item</th><th>Store</th><th className="num">Quantity</th><th>Unit</th><th className="num">Valuation rate</th><th className="num">Value</th><th /></tr>
          </thead>
          <tbody>
            {data?.items.map((r) => (
              <tr key={`${r.productId}-${r.storeId}`} className={r.quantity < 10 ? 'low' : undefined}>
                <td>{r.sku}</td><td>{r.name}</td><td>{r.store}</td>
                <td className="num">{num.format(r.quantity)}{r.quantity < 10 && <span className="tag warn">Low</span>}</td>
                <td>{r.uom ?? '—'}</td>
                <td className="num">{rate.format(r.valuationRate)}</td><td className="num">{inr.format(r.stockValue)}</td>
                <td><Link to={`/reports/stock?productId=${r.productId}&storeId=${r.storeId}`}>Ledger</Link></td>
              </tr>
            ))}
          </tbody>
        </table>
        {data && data.items.length === 0 && <p className="hint">Nothing in stock for these filters.</p>}
        {data && <Pager page={f.page} total={data.total} pageSize={data.pageSize} onPage={(page) => setF({ ...f, page })} />}
      </div>
    </>
  );
}

/** Reports › Stock Ledger: every stock movement, newest first, with the balance after it. */
export function StockLedgerPage() {
  const { stores, items } = useLookups();
  const [initial] = useSearchParams();
  const [f, setF] = useState({
    productId: initial.get('productId') ?? '', storeId: initial.get('storeId') ?? '', from: '', to: '', page: 1,
  });
  const [data, setData] = useState<PagedResult<StockLedgerRow> | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let alive = true;
    api.stockLedger(f).then((d) => alive && setData(d)).catch((e) => alive && setError(errorText(e)));
    return () => { alive = false; };
  }, [f]);

  return (
    <>
      <h1>Stock ledger</h1>
      {error && <p role="alert" className="error">{error}</p>}
      <div className="card toolbar">
        <label className="grow">Item
          <select aria-label="Item" value={f.productId} onChange={(e) => setF({ ...f, productId: e.target.value, page: 1 })}>
            <option value="">All items</option>{items.map((i) => <option key={i.id} value={i.id}>{i.sku} · {i.name}</option>)}
          </select>
        </label>
        <label>Store
          <select aria-label="Store" value={f.storeId} onChange={(e) => setF({ ...f, storeId: e.target.value, page: 1 })}>
            <option value="">All stores</option>{stores.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
          </select>
        </label>
        <label>From<input type="date" value={f.from} onChange={(e) => setF({ ...f, from: e.target.value, page: 1 })} /></label>
        <label>To<input type="date" value={f.to} onChange={(e) => setF({ ...f, to: e.target.value, page: 1 })} /></label>
      </div>
      <div className="card table-wrap">
        <table aria-label="Stock ledger">
          <thead>
            <tr><th>Date</th><th>Item</th><th>Store</th><th>Voucher</th><th className="num">In / out</th><th className="num">Balance</th>
              <th className="num">Rate</th><th className="num">Valuation rate</th><th className="num">Value change</th><th className="num">Balance value</th></tr>
          </thead>
          <tbody>
            {data?.items.map((r) => (
              <tr key={r.id} className={r.isReversal ? 'reversal' : undefined}>
                <td>{fmtDate(r.postingDate)}</td>
                <td>{r.sku} · {r.productName}</td><td>{r.store}</td>
                <td>
                  {r.voucherType}{' '}
                  {r.voucherType === 'Stock Entry' && r.voucherId ? <Link to={`${ENTRY_ROUTE}/${r.voucherId}`}>{r.voucherNo}</Link>
                    : r.voucherType === 'Opening' && r.voucherNo === 'OPENING' ? null : r.voucherNo}
                  {r.isReversal && <span className="tag">Reversal</span>}
                </td>
                <td className={`num ${r.qtyChange < 0 ? 'out' : 'in'}`}>{signed(r.qtyChange)}</td>
                <td className="num">{num.format(r.qtyAfter)}</td>
                <td className="num">{rate.format(r.rate)}</td><td className="num">{rate.format(r.valuationRate)}</td>
                <td className="num">{inr.format(r.valueChange)}</td><td className="num">{inr.format(r.valueAfter)}</td>
              </tr>
            ))}
          </tbody>
        </table>
        {data && data.items.length === 0 && <p className="hint">No stock movements for these filters.</p>}
        {data && <Pager page={f.page} total={data.total} pageSize={data.pageSize} onPage={(page) => setF({ ...f, page })} />}
      </div>
    </>
  );
}
