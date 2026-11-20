import { useCallback, useEffect, useMemo, useState, type FormEvent } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import {
  api, type DocStatus, type ItemLookup, type PagedResult, type StockEntry, type StockEntrySummary, type StockEntryType,
  type Store, type UpsertStockEntry,
} from '../../api/client';
import { usePermissions } from '../../auth/PermissionsContext';
import { errorText, fmtDate, inr, num, rate, today } from './format';

const PAGE = 'stock-addition';
export const ENTRY_ROUTE = '/inventory/stock-addition';
const TYPES: { value: StockEntryType; label: string; help: string }[] = [
  { value: 'Receipt', label: 'Receipt', help: 'Stock comes into a store at the rate you enter.' },
  { value: 'Issue', label: 'Issue', help: 'Stock goes out of a store at its moving-average rate.' },
  { value: 'Transfer', label: 'Transfer', help: 'Stock moves between two stores at the source store’s rate.' },
];

export function StatusPill({ status }: { status: DocStatus }) {
  return <span className={`status status-${status.toLowerCase()}`}>{status}</span>;
}

/** Inventory › Stock Movement › Stock Entries: the list. */
export function StockEntriesPage() {
  const { can } = usePermissions();
  const [filter, setFilter] = useState<{ status: string; type: string; page: number }>({ status: '', type: '', page: 1 });
  const [data, setData] = useState<PagedResult<StockEntrySummary> | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let alive = true;
    api.stockEntries(filter).then((d) => alive && setData(d)).catch((e) => alive && setError(errorText(e)));
    return () => { alive = false; };
  }, [filter]);

  const pages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1;
  return (
    <>
      <div className="page-head">
        <h1>Stock entries</h1>
        {can(PAGE, 'write') && <Link className="button" to={`${ENTRY_ROUTE}/new`}>New stock entry</Link>}
      </div>
      {error && <p role="alert" className="error">{error}</p>}
      <div className="card toolbar">
        <label>Status
          <select aria-label="Status" value={filter.status} onChange={(e) => setFilter({ ...filter, status: e.target.value, page: 1 })}>
            <option value="">All</option><option>Draft</option><option>Submitted</option><option>Cancelled</option>
          </select>
        </label>
        <label>Type
          <select aria-label="Type" value={filter.type} onChange={(e) => setFilter({ ...filter, type: e.target.value, page: 1 })}>
            <option value="">All</option>{TYPES.map((t) => <option key={t.value}>{t.value}</option>)}
          </select>
        </label>
      </div>
      <div className="card table-wrap">
        <table aria-label="Stock entries">
          <thead>
            <tr><th>Number</th><th>Date</th><th>Type</th><th>From</th><th>To</th><th className="num">Items</th><th className="num">Value</th><th>Status</th></tr>
          </thead>
          <tbody>
            {data?.items.map((e) => (
              <tr key={e.id}>
                <td><Link to={`${ENTRY_ROUTE}/${e.id}`}>{e.number}</Link></td>
                <td>{fmtDate(e.postingDate)}</td><td>{e.type}</td>
                <td>{e.sourceStore ?? '—'}</td><td>{e.targetStore ?? '—'}</td>
                <td className="num">{e.lineCount}</td>
                <td className="num">{e.status === 'Draft' && e.type !== 'Receipt' ? '—' : inr.format(e.totalValue)}</td>
                <td><StatusPill status={e.status} /></td>
              </tr>
            ))}
          </tbody>
        </table>
        {data && data.items.length === 0 && <p className="hint">No stock entries yet.</p>}
        {data && pages > 1 && (
          <div className="pager">
            <button className="link" disabled={filter.page <= 1} onClick={() => setFilter({ ...filter, page: filter.page - 1 })}>‹ Previous</button>
            <span>Page {filter.page} of {pages}</span>
            <button className="link" disabled={filter.page >= pages} onClick={() => setFilter({ ...filter, page: filter.page + 1 })}>Next ›</button>
          </div>
        )}
      </div>
    </>
  );
}

interface LineForm { productId: string; quantity: string; rate: string }
const emptyLine = (): LineForm => ({ productId: '', quantity: '1', rate: '' });

/** /inventory/stock-addition/new and /:id — edit a draft, or view a submitted/cancelled entry. */
export function StockEntryPage() {
  const { id } = useParams();
  const isNew = !id || id === 'new';
  const navigate = useNavigate();
  const { can } = usePermissions();
  const [entry, setEntry] = useState<StockEntry | null>(null);
  const [stores, setStores] = useState<Store[]>([]);
  const [items, setItems] = useState<ItemLookup[]>([]);
  const [form, setForm] = useState({ type: 'Receipt' as StockEntryType, postingDate: today(), sourceStoreId: '', targetStoreId: '', remarks: '' });
  const [lines, setLines] = useState<LineForm[]>([emptyLine()]);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    Promise.all([api.lookupStores(), api.lookupItems()]).then(([s, i]) => {
      setStores(s);
      setItems(i);
      const main = s.find((x) => x.isDefault)?.id ?? '';
      if (isNew) setForm((f) => ({ ...f, targetStoreId: f.targetStoreId || main, sourceStoreId: f.sourceStoreId || main }));
    }).catch((e) => setError(errorText(e)));
  }, [isNew]);

  const fill = useCallback((e: StockEntry) => {
    setEntry(e);
    setForm({ type: e.type, postingDate: e.postingDate, sourceStoreId: e.sourceStoreId ?? '', targetStoreId: e.targetStoreId ?? '', remarks: e.remarks ?? '' });
    setLines(e.lines.map((l) => ({ productId: l.productId, quantity: String(l.quantity), rate: e.type === 'Receipt' ? String(l.rate) : '' })));
  }, []);

  useEffect(() => {
    if (!isNew) api.stockEntry(id!).then(fill).catch((e) => setError(errorText(e)));
  }, [id, isNew, fill]);

  const editable = isNew ? can(PAGE, 'write') : entry?.status === 'Draft' && can(PAGE, 'update');
  const itemById = useMemo(() => new Map(items.map((i) => [i.id, i])), [items]);
  const isReceipt = form.type === 'Receipt';
  const total = lines.reduce((s, l) => s + (Number(l.quantity) || 0) * (Number(l.rate) || 0), 0);

  const payload = (): UpsertStockEntry => ({
    type: form.type, postingDate: form.postingDate || null,
    sourceStoreId: form.type === 'Receipt' ? null : form.sourceStoreId || null,
    targetStoreId: form.type === 'Issue' ? null : form.targetStoreId || null,
    remarks: form.remarks.trim() || null,
    lines: lines.filter((l) => l.productId).map((l) => ({
      productId: l.productId, quantity: Number(l.quantity), rate: isReceipt ? Number(l.rate) : null,
    })),
  });

  async function act(label: string, work: () => Promise<StockEntry | void>, after?: (e: StockEntry | void) => void) {
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      const result = await work();
      if (result) fill(result);
      after?.(result);
      setNotice(label);
    } catch (e) {
      setError(errorText(e));
    } finally {
      setBusy(false);
    }
  }

  async function save(e: FormEvent) {
    e.preventDefault();
    await act('Draft saved.', () => (isNew ? api.createStockEntry(payload()) : api.updateStockEntry(entry!.id, payload())),
      (saved) => { if (isNew && saved) navigate(`${ENTRY_ROUTE}/${saved.id}`, { replace: true }); });
  }

  const setLine = (i: number, patch: Partial<LineForm>) => setLines(lines.map((l, j) => (j === i ? { ...l, ...patch } : l)));
  const typeHelp = TYPES.find((t) => t.value === form.type)!.help;

  return (
    <>
      <p className="crumbs"><Link to={ENTRY_ROUTE}>Stock entries</Link> › {isNew ? 'New' : entry?.number ?? '…'}</p>
      <div className="page-head">
        <h1>{isNew ? 'New stock entry' : entry?.number ?? 'Stock entry'} {entry && <StatusPill status={entry.status} />}</h1>
        {entry && (
          <div className="row-actions">
            {entry.status === 'Draft' && can(PAGE, 'update') && (
              <button disabled={busy} onClick={() => act('Submitted. Stock has been updated.', () => api.submitStockEntry(entry.id))}>Submit</button>
            )}
            {entry.status === 'Draft' && can(PAGE, 'delete') && (
              <button className="link danger" disabled={busy} onClick={() => confirm(`Delete draft ${entry.number}?`)
                && act('Deleted.', () => api.deleteStockEntry(entry.id), () => navigate(ENTRY_ROUTE))}>Delete draft</button>
            )}
            {entry.status === 'Submitted' && can(PAGE, 'delete') && (
              <button className="link danger" disabled={busy} onClick={() => confirm(`Cancel ${entry.number}? Its stock movements will be reversed.`)
                && act('Cancelled. Stock movements were reversed.', () => api.cancelStockEntry(entry.id))}>Cancel entry</button>
            )}
          </div>
        )}
      </div>
      {error && <p role="alert" className="error">{error}</p>}
      {notice && <p role="status" className="ok">{notice}</p>}

      <form className="card" onSubmit={save} aria-label="Stock entry">
        <fieldset disabled={!editable || busy} className="form-grid">
          <label>Type
            <select aria-label="Type" value={form.type} onChange={(e) => setForm({ ...form, type: e.target.value as StockEntryType })}>
              {TYPES.map((t) => <option key={t.value} value={t.value}>{t.label}</option>)}
            </select>
          </label>
          <label>Posting date<input type="date" required max={today()} value={form.postingDate}
            onChange={(e) => setForm({ ...form, postingDate: e.target.value })} /></label>
          {!isReceipt && (
            <label>From store
              <select aria-label="From store" required value={form.sourceStoreId} onChange={(e) => setForm({ ...form, sourceStoreId: e.target.value })}>
                <option value="">Choose…</option>{stores.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
              </select>
            </label>
          )}
          {form.type !== 'Issue' && (
            <label>To store
              <select aria-label="To store" required value={form.targetStoreId} onChange={(e) => setForm({ ...form, targetStoreId: e.target.value })}>
                <option value="">Choose…</option>{stores.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
              </select>
            </label>
          )}
          <p className="hint span-all">{typeHelp}</p>
        </fieldset>

        <div className="table-wrap">
          <table aria-label="Items" className="lines">
            <thead>
              <tr><th>#</th><th>Item</th><th className="num">Quantity</th><th className="num">Rate</th><th className="num">Amount</th>{editable && <th />}</tr>
            </thead>
            <tbody>
              {lines.map((l, i) => {
                const posted = entry?.status !== 'Draft' ? entry?.lines[i] : undefined;
                const item = itemById.get(l.productId);
                const amount = posted ? posted.amount : (Number(l.quantity) || 0) * (Number(l.rate) || 0);
                return (
                  <tr key={i}>
                    <td>{i + 1}</td>
                    <td>
                      {editable ? (
                        <select aria-label={`Item ${i + 1}`} required value={l.productId}
                          onChange={(e) => setLine(i, { productId: e.target.value, rate: l.rate || String(itemById.get(e.target.value)?.unitPrice ?? '') })}>
                          <option value="">Choose an item…</option>
                          {items.map((it) => <option key={it.id} value={it.id}>{it.sku} · {it.name}</option>)}
                        </select>
                      ) : <>{posted?.sku ?? item?.sku} · {posted?.productName ?? item?.name}</>}
                      {item && editable && <div className="hint">In stock (all stores): {num.format(item.stockQuantity)} {item.uom ?? ''}</div>}
                    </td>
                    <td className="num">
                      {editable ? <input aria-label={`Quantity ${i + 1}`} type="number" min={1} step={1} required value={l.quantity}
                        onChange={(e) => setLine(i, { quantity: e.target.value })} /> : num.format(Number(l.quantity))}
                    </td>
                    <td className="num">
                      {editable && isReceipt ? <input aria-label={`Rate ${i + 1}`} type="number" min={0} step="0.01" required value={l.rate}
                        onChange={(e) => setLine(i, { rate: e.target.value })} />
                        : posted ? rate.format(posted.rate) : isReceipt ? rate.format(Number(l.rate) || 0) : <span className="hint">at moving average</span>}
                    </td>
                    <td className="num">{!posted && !isReceipt ? '—' : inr.format(amount)}</td>
                    {editable && (
                      <td><button type="button" className="link danger" aria-label={`Remove line ${i + 1}`} disabled={lines.length === 1}
                        onClick={() => setLines(lines.filter((_, j) => j !== i))}>Remove</button></td>
                    )}
                  </tr>
                );
              })}
            </tbody>
            <tfoot>
              <tr><td colSpan={4}>Total</td><td className="num">
                {entry && entry.status !== 'Draft' ? inr.format(entry.totalValue) : isReceipt ? inr.format(total) : '—'}
              </td>{editable && <td />}</tr>
            </tfoot>
          </table>
        </div>
        {editable && <button type="button" className="link" onClick={() => setLines([...lines, emptyLine()])}>+ Add item</button>}

        <fieldset disabled={!editable || busy} className="form-grid">
          <label className="span-all">Remarks<input maxLength={500} value={form.remarks} onChange={(e) => setForm({ ...form, remarks: e.target.value })} /></label>
        </fieldset>
        {editable && (
          <div className="actions">
            <button disabled={busy}>{busy ? 'Saving…' : 'Save draft'}</button>
            <span className="hint">Nothing moves until the entry is submitted.</span>
          </div>
        )}
        {entry?.status === 'Submitted' && entry.submittedAt && <p className="hint">Submitted {new Date(entry.submittedAt).toLocaleString()}.</p>}
        {entry?.status === 'Cancelled' && entry.cancelledAt && <p className="hint">Cancelled {new Date(entry.cancelledAt).toLocaleString()}; its movements were reversed.</p>}
      </form>
    </>
  );
}
