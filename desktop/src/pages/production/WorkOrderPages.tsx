import { useCallback, useEffect, useMemo, useState, type FormEvent } from 'react';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom';
import {
  api, type CustomerPo, type DocumentLink, type PagedResult, type UpsertWorkOrder, type WorkOrder, type WorkOrderSummary,
} from '../../api/client';
import { usePermissions } from '../../auth/PermissionsContext';
import { errorText, fmtDate, inr, num, rate, today } from '../inventory/format';
import { DocPill, PO_ROUTE, Pager, WO_ROUTE, useDocAction, useSalesLookups } from '../sales/shared';

const PAGE = 'work-orders';
const statusLabel = (s: string) => (s === 'Submitted' ? 'Completed' : s);

/** Production › Work Orders: production of items, usually for a customer P.O. line. */
export function WorkOrdersPage() {
  const { can } = usePermissions();
  const [f, setF] = useState({ status: '', search: '', page: 1 });
  const [search, setSearch] = useState('');
  const [data, setData] = useState<PagedResult<WorkOrderSummary> | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => { const t = setTimeout(() => setF((x) => (x.search === search ? x : { ...x, search, page: 1 })), 300); return () => clearTimeout(t); }, [search]);
  useEffect(() => {
    let alive = true;
    api.workOrders(f).then((d) => alive && setData(d)).catch((e) => alive && setError(errorText(e)));
    return () => { alive = false; };
  }, [f]);

  return (
    <>
      <div className="page-head">
        <h1>Work orders</h1>
        {can(PAGE, 'write') && <Link className="button" to={`${WO_ROUTE}/new`}>New work order</Link>}
      </div>
      {error && <p role="alert" className="error">{error}</p>}
      <div className="card toolbar">
        <label>Status
          <select aria-label="Status" value={f.status} onChange={(e) => setF({ ...f, status: e.target.value, page: 1 })}>
            <option value="">All</option><option value="Draft">Draft</option><option value="Submitted">Completed</option><option>Cancelled</option>
          </select>
        </label>
        <label className="grow">Search<input type="search" placeholder="Work order, item or P.O." value={search} onChange={(e) => setSearch(e.target.value)} /></label>
      </div>
      <div className="card table-wrap">
        <table aria-label="Work orders">
          <thead>
            <tr><th>Number</th><th>Date</th><th>Item</th><th className="num">Quantity</th><th>For P.O.</th><th>Customer</th><th>Store</th>
              <th className="num">Cost</th><th>Status</th></tr>
          </thead>
          <tbody>
            {data?.items.map((w) => (
              <tr key={w.id}>
                <td><Link to={`${WO_ROUTE}/${w.id}`}>{w.number}</Link></td>
                <td>{fmtDate(w.postingDate)}</td><td>{w.sku} · {w.productName}</td><td className="num">{num.format(w.quantity)}</td>
                <td>{w.customerPoNumber ?? 'Stock'}</td><td>{w.customerName ?? '—'}</td><td>{w.store}</td>
                <td className="num">{w.status === 'Submitted' ? inr.format(w.totalCost) : '—'}</td>
                <td><DocPill status={w.status} label={statusLabel(w.status)} /></td>
              </tr>
            ))}
          </tbody>
        </table>
        {data && data.items.length === 0 && <p className="hint">No work orders yet.</p>}
        {data && <Pager page={f.page} total={data.total} pageSize={data.pageSize} onPage={(page) => setF({ ...f, page })} />}
      </div>
    </>
  );
}

interface MaterialForm { productId: string; quantity: string }

/**
 * /production/work-orders/new[?poId=&lineId=] and /:id. Completing (submitting) a work order takes the materials out of the
 * material store and puts the produced item into the output store at (materials + other cost) / quantity.
 */
export function WorkOrderPage() {
  const { id } = useParams();
  const isNew = !id || id === 'new';
  const [query] = useSearchParams();
  const navigate = useNavigate();
  const { can } = usePermissions();
  const { items, stores, lookupError } = useSalesLookups({ items: true, stores: true });
  const [wo, setWo] = useState<WorkOrder | null>(null);
  const [openPos, setOpenPos] = useState<DocumentLink[]>([]);
  const [po, setPo] = useState<CustomerPo | null>(null);
  const [form, setForm] = useState({
    poId: query.get('poId') ?? '', lineId: query.get('lineId') ?? '', productId: '', quantity: '1', storeId: '', materialStoreId: '',
    postingDate: today(), otherCost: '0', remarks: '',
  });
  const [materials, setMaterials] = useState<MaterialForm[]>([]);
  const { busy, error, setError, notice, act } = useDocAction();

  const fill = useCallback((w: WorkOrder) => {
    setWo(w);
    setForm({ poId: w.customerPoId ?? '', lineId: w.customerPoLineId ?? '', productId: w.productId, quantity: String(w.quantity),
      storeId: w.storeId, materialStoreId: w.materialStoreId, postingDate: w.postingDate, otherCost: String(w.otherCost), remarks: w.remarks ?? '' });
    setMaterials(w.materials.map((m) => ({ productId: m.productId, quantity: String(m.quantity) })));
  }, []);
  useEffect(() => { if (!isNew) api.workOrder(id!).then(fill).catch((e) => setError(errorText(e))); }, [id, isNew, fill, setError]);
  useEffect(() => { if (isNew) api.lookupOpenPos().then(setOpenPos).catch(() => undefined); }, [isNew]);
  useEffect(() => {
    const main = stores.find((s) => s.isDefault)?.id ?? '';
    if (isNew && main) setForm((f) => ({ ...f, storeId: f.storeId || main, materialStoreId: f.materialStoreId || main }));
  }, [stores, isNew]);

  // The chosen P.O.: its lines to produce for.
  useEffect(() => {
    if (!form.poId) { setPo(null); return; }
    let alive = true;
    api.customerPo(form.poId).then((p) => {
      if (!alive) return;
      setPo(p);
      if (isNew) setForm((f) => {
        const line = p.lines.find((l) => l.id === f.lineId) ?? (p.lines.length === 1 ? p.lines[0] : undefined);
        return line ? { ...f, lineId: line.id, productId: line.productId, quantity: String(Math.max(1, line.toProduce)) } : f;
      });
    }).catch((e) => alive && setError(errorText(e)));
    return () => { alive = false; };
  }, [form.poId, isNew, setError]);

  const editable = isNew ? can(PAGE, 'write') : wo?.status === 'Draft' && can(PAGE, 'update');
  const itemById = useMemo(() => new Map(items.map((i) => [i.id, i])), [items]);
  const line = po?.lines.find((l) => l.id === form.lineId);
  const product = itemById.get(form.productId);

  const payload = (): UpsertWorkOrder => ({
    customerPoLineId: form.lineId || null, productId: form.productId || null, quantity: Number(form.quantity),
    storeId: form.storeId, materialStoreId: form.materialStoreId || null, postingDate: form.postingDate || null,
    otherCost: Number(form.otherCost) || 0, remarks: form.remarks.trim() || null,
    materials: materials.filter((m) => m.productId).map((m) => ({ productId: m.productId, quantity: Number(m.quantity) })),
  });

  async function save(e: FormEvent) {
    e.preventDefault();
    await act('Draft saved.', () => (isNew ? api.createWorkOrder(payload()) : api.updateWorkOrder(wo!.id, payload())), (saved) => {
      fill(saved);
      if (isNew) navigate(`${WO_ROUTE}/${saved.id}`, { replace: true });
    });
  }

  const setMat = (i: number, patch: Partial<MaterialForm>) => setMaterials(materials.map((m, j) => (j === i ? { ...m, ...patch } : m)));
  const done = wo && wo.status !== 'Draft';

  return (
    <>
      <p className="crumbs"><Link to={WO_ROUTE}>Work orders</Link> › {isNew ? 'New' : wo?.number ?? '…'}</p>
      <div className="page-head">
        <h1>{isNew ? 'New work order' : wo?.number ?? 'Work order'} {wo && <DocPill status={wo.status} label={statusLabel(wo.status)} />}</h1>
        {wo && (
          <div className="row-actions">
            <Link className="button" to={`${WO_ROUTE}/${wo.id}/print`}>Print</Link>
            {wo.status === 'Draft' && can(PAGE, 'update') && (
              <button disabled={busy} onClick={() => act('Production completed. Stock has been updated.', () => api.submitWorkOrder(wo.id), fill)}>Complete production</button>
            )}
            {wo.status === 'Draft' && can(PAGE, 'delete') && (
              <button className="link danger" disabled={busy} onClick={() => confirm(`Delete draft ${wo.number}?`)
                && act('Deleted.', () => api.deleteWorkOrder(wo.id), () => navigate(WO_ROUTE))}>Delete draft</button>
            )}
            {wo.status === 'Submitted' && can(PAGE, 'delete') && (
              <button className="link danger" disabled={busy} onClick={() => confirm(`Cancel ${wo.number}? The produced stock goes out and the materials come back.`)
                && act('Cancelled. Stock movements were reversed.', () => api.cancelWorkOrder(wo.id), fill)}>Cancel work order</button>
            )}
          </div>
        )}
      </div>
      {(error || lookupError) && <p role="alert" className="error">{error ?? lookupError}</p>}
      {notice && <p role="status" className="ok">{notice}</p>}

      <form className="card" onSubmit={save} aria-label="Work order">
        <fieldset disabled={!editable || busy} className="form-grid">
          {isNew ? (
            <label>For customer P.O.
              <select aria-label="Customer P.O." value={form.poId}
                onChange={(e) => setForm({ ...form, poId: e.target.value, lineId: '', productId: e.target.value ? '' : form.productId })}>
                <option value="">— Produce for stock —</option>
                {openPos.map((p) => <option key={p.id} value={p.id}>{p.number} · {p.detail}</option>)}
                {form.poId && !openPos.some((p) => p.id === form.poId) && po && <option value={po.id}>{po.number} · {po.customerName}</option>}
              </select>
            </label>
          ) : (
            <p className="span-all">{wo?.customerPoId
              ? <>For <Link to={`${PO_ROUTE}/${wo.customerPoId}`}>{wo.customerPoNumber}</Link> · {wo.customerName}</> : 'Produced for stock'}</p>
          )}
          {po && (
            <label>P.O. line
              <select aria-label="P.O. line" required value={form.lineId} onChange={(e) => {
                const l = po.lines.find((x) => x.id === e.target.value);
                setForm({ ...form, lineId: e.target.value, productId: l?.productId ?? '', quantity: String(Math.max(1, l?.toProduce ?? 1)) });
              }}>
                <option value="">Choose…</option>
                {po.lines.map((l) => <option key={l.id} value={l.id}>{l.lineNo}. {l.sku} · {l.productName} ({l.toProduce} to produce)</option>)}
              </select>
            </label>
          )}
          {!form.poId && (
            <label>Item to produce
              <select aria-label="Item to produce" required value={form.productId} onChange={(e) => setForm({ ...form, productId: e.target.value })}>
                <option value="">Choose…</option>
                {items.filter((i) => i.itemType !== 'Raw Material' && i.itemType !== 'Consumable').map((i) =>
                  <option key={i.id} value={i.id}>{i.sku} · {i.name}</option>)}
                {wo && !itemById.has(wo.productId) && <option value={wo.productId}>{wo.sku} · {wo.productName}</option>}
              </select>
            </label>
          )}
          <label>Quantity<input type="number" min={1} step={1} required value={form.quantity} onChange={(e) => setForm({ ...form, quantity: e.target.value })} /></label>
          <label>Output store
            <select aria-label="Output store" required value={form.storeId} onChange={(e) => setForm({ ...form, storeId: e.target.value })}>
              <option value="">Choose…</option>{stores.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
            </select>
          </label>
          <label>Material store
            <select aria-label="Material store" value={form.materialStoreId} onChange={(e) => setForm({ ...form, materialStoreId: e.target.value })}>
              {stores.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
            </select>
          </label>
          <label>Production date<input type="date" required max={today()} value={form.postingDate} onChange={(e) => setForm({ ...form, postingDate: e.target.value })} /></label>
          <label>Labour &amp; overhead cost<input type="number" min={0} step="0.01" value={form.otherCost} onChange={(e) => setForm({ ...form, otherCost: e.target.value })} /></label>
          {line && <p className="hint span-all">P.O. line {line.lineNo}: ordered {num.format(line.quantity)}, produced {num.format(line.producedQty)}, {num.format(line.toProduce)} still to produce.</p>}
          {!line && product && <p className="hint span-all">{product.sku} · {product.name}: {num.format(product.stockQuantity)} in stock.</p>}
        </fieldset>

        <h2>Materials consumed</h2>
        <div className="table-wrap">
          <table aria-label="Materials" className="lines">
            <thead>
              <tr><th>#</th><th>Material</th><th className="num">Quantity</th><th className="num">Rate</th><th className="num">Amount</th>{editable && <th />}</tr>
            </thead>
            <tbody>
              {materials.map((m, i) => {
                const posted = done ? wo!.materials[i] : undefined;
                const it = itemById.get(m.productId);
                return (
                  <tr key={i}>
                    <td>{i + 1}</td>
                    <td>
                      {editable ? (
                        <select aria-label={`Material ${i + 1}`} required value={m.productId} onChange={(e) => setMat(i, { productId: e.target.value })}>
                          <option value="">Choose a material…</option>
                          {items.filter((x) => x.id !== form.productId).map((x) => <option key={x.id} value={x.id}>{x.sku} · {x.name}</option>)}
                        </select>
                      ) : <>{posted?.sku ?? wo?.materials[i]?.sku} · {posted?.productName ?? wo?.materials[i]?.productName}</>}
                      {it && editable && <div className="hint">In stock (all stores): {num.format(it.stockQuantity)} {it.uom ?? ''}</div>}
                    </td>
                    <td className="num">{editable ? <input aria-label={`Material quantity ${i + 1}`} type="number" min={1} step={1} required value={m.quantity}
                      onChange={(e) => setMat(i, { quantity: e.target.value })} /> : num.format(Number(m.quantity))}</td>
                    <td className="num">{posted ? rate.format(posted.rate) : <span className="hint">at moving average</span>}</td>
                    <td className="num">{posted ? inr.format(posted.amount) : '—'}</td>
                    {editable && <td><button type="button" className="link danger" aria-label={`Remove material ${i + 1}`}
                      onClick={() => setMaterials(materials.filter((_, j) => j !== i))}>Remove</button></td>}
                  </tr>
                );
              })}
              {materials.length === 0 && <tr><td colSpan={6} className="hint">No materials: the item comes in at the labour &amp; overhead cost only.</td></tr>}
            </tbody>
          </table>
        </div>
        {editable && <button type="button" className="link" onClick={() => setMaterials([...materials, { productId: '', quantity: '1' }])}>+ Add material</button>}

        {done && (
          <section className="stats" aria-label="Production cost">
            <div className="card stat"><span>Materials</span><strong>{inr.format(wo!.materialCost)}</strong></div>
            <div className="card stat"><span>Labour &amp; overhead</span><strong>{inr.format(wo!.otherCost)}</strong></div>
            <div className="card stat"><span>Total cost</span><strong>{inr.format(wo!.totalCost)}</strong></div>
            <div className="card stat"><span>Cost per unit</span><strong>{rate.format(wo!.unitCost)}</strong></div>
          </section>
        )}

        <fieldset disabled={!editable || busy} className="form-grid">
          <label className="span-all">Remarks<input maxLength={500} value={form.remarks} onChange={(e) => setForm({ ...form, remarks: e.target.value })} /></label>
        </fieldset>
        {editable && (
          <div className="actions">
            <button disabled={busy}>{busy ? 'Saving…' : 'Save draft'}</button>
            <span className="hint">Nothing moves until production is completed.</span>
          </div>
        )}
        {wo?.status === 'Submitted' && wo.submittedAt && <p className="hint">Completed {new Date(wo.submittedAt).toLocaleString()}.</p>}
        {wo?.status === 'Cancelled' && wo.cancelledAt && <p className="hint">Cancelled {new Date(wo.cancelledAt).toLocaleString()}; its movements were reversed.</p>}
      </form>
    </>
  );
}
