import { useCallback, useEffect, useMemo, useState, type FormEvent } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { api, type CustomerPo, type CustomerPoSummary, type PagedResult, type UpsertCustomerPo } from '../../api/client';
import { usePermissions } from '../../auth/PermissionsContext';
import { errorText, fmtDate, inr, num, today } from '../inventory/format';
import { DocPill, INVOICE_ROUTE, PO_ROUTE, Pager, WO_ROUTE, useDocAction, useSalesLookups } from './shared';

const PAGE = 'customer-pos';

/** Sales › Orders › Customer P.O.: purchase orders received from customers. */
export function CustomerPosPage() {
  const { can } = usePermissions();
  const [f, setF] = useState({ status: '', search: '', page: 1 });
  const [search, setSearch] = useState('');
  const [data, setData] = useState<PagedResult<CustomerPoSummary> | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => { const t = setTimeout(() => setF((x) => (x.search === search ? x : { ...x, search, page: 1 })), 300); return () => clearTimeout(t); }, [search]);
  useEffect(() => {
    let alive = true;
    api.customerPos(f).then((d) => alive && setData(d)).catch((e) => alive && setError(errorText(e)));
    return () => { alive = false; };
  }, [f]);

  return (
    <>
      <div className="page-head">
        <h1>Customer P.O.</h1>
        {can(PAGE, 'write') && <Link className="button" to={`${PO_ROUTE}/new`}>New P.O.</Link>}
      </div>
      {error && <p role="alert" className="error">{error}</p>}
      <div className="card toolbar">
        <label>Status
          <select aria-label="Status" value={f.status} onChange={(e) => setF({ ...f, status: e.target.value, page: 1 })}>
            <option value="">All</option><option>Draft</option><option>Confirmed</option><option>Completed</option><option>Cancelled</option>
          </select>
        </label>
        <label className="grow">Search<input type="search" placeholder="P.O. number, customer or their P.O. no." value={search}
          onChange={(e) => setSearch(e.target.value)} /></label>
      </div>
      <div className="card table-wrap">
        <table aria-label="Customer P.O.s">
          <thead>
            <tr><th>Number</th><th>Date</th><th>Customer</th><th>Customer P.O. no.</th><th>Delivery</th>
              <th className="num">Ordered</th><th className="num">Produced</th><th className="num">Invoiced</th><th className="num">Total</th><th>Status</th></tr>
          </thead>
          <tbody>
            {data?.items.map((p) => (
              <tr key={p.id}>
                <td><Link to={`${PO_ROUTE}/${p.id}`}>{p.number}</Link></td>
                <td>{fmtDate(p.poDate)}</td><td>{p.customerName}</td><td>{p.customerPoNo}</td>
                <td>{p.deliveryDate ? fmtDate(p.deliveryDate) : '—'}</td>
                <td className="num">{num.format(p.orderedQty)}</td><td className="num">{num.format(p.producedQty)}</td>
                <td className="num">{num.format(p.invoicedQty)}</td><td className="num">{inr.format(p.grandTotal)}</td>
                <td><DocPill status={p.status} /></td>
              </tr>
            ))}
          </tbody>
        </table>
        {data && data.items.length === 0 && <p className="hint">No customer P.O.s yet.</p>}
        {data && <Pager page={f.page} total={data.total} pageSize={data.pageSize} onPage={(page) => setF({ ...f, page })} />}
      </div>
    </>
  );
}

interface LineForm { productId: string; quantity: string; rate: string; taxRate: string }
const emptyLine = (): LineForm => ({ productId: '', quantity: '1', rate: '', taxRate: '' });

/** /sales/customer-po/new and /:id — edit a draft; confirm it; then raise work orders and invoices from it. */
export function CustomerPoPage() {
  const { id } = useParams();
  const isNew = !id || id === 'new';
  const navigate = useNavigate();
  const { can } = usePermissions();
  const { customers, items, lookupError } = useSalesLookups({ customers: true, items: true });
  const [po, setPo] = useState<CustomerPo | null>(null);
  const [form, setForm] = useState({ customerId: '', customerPoNo: '', poDate: today(), deliveryDate: '', remarks: '' });
  const [lines, setLines] = useState<LineForm[]>([emptyLine()]);
  const { busy, error, setError, notice, act } = useDocAction();

  const fill = useCallback((p: CustomerPo) => {
    setPo(p);
    setForm({ customerId: p.customerId, customerPoNo: p.customerPoNo, poDate: p.poDate, deliveryDate: p.deliveryDate ?? '', remarks: p.remarks ?? '' });
    setLines(p.lines.map((l) => ({ productId: l.productId, quantity: String(l.quantity), rate: String(l.rate), taxRate: String(l.taxRate) })));
  }, []);
  useEffect(() => { if (!isNew) api.customerPo(id!).then(fill).catch((e) => setError(errorText(e))); }, [id, isNew, fill, setError]);

  const editable = isNew ? can(PAGE, 'write') : po?.status === 'Draft' && can(PAGE, 'update');
  const itemById = useMemo(() => new Map(items.map((i) => [i.id, i])), [items]);
  const lineTotals = lines.map((l) => {
    const amount = (Number(l.quantity) || 0) * (Number(l.rate) || 0);
    return { amount, tax: amount * (Number(l.taxRate) || 0) / 100 };
  });
  const sub = lineTotals.reduce((s, t) => s + t.amount, 0);
  const tax = lineTotals.reduce((s, t) => s + t.tax, 0);

  const payload = (): UpsertCustomerPo => ({
    customerId: form.customerId, customerPoNo: form.customerPoNo.trim(), poDate: form.poDate || null,
    deliveryDate: form.deliveryDate || null, remarks: form.remarks.trim() || null,
    lines: lines.filter((l) => l.productId).map((l) => ({
      productId: l.productId, quantity: Number(l.quantity), rate: Number(l.rate), taxRate: l.taxRate === '' ? null : Number(l.taxRate),
    })),
  });

  async function save(e: FormEvent) {
    e.preventDefault();
    await act('Draft saved.', () => (isNew ? api.createCustomerPo(payload()) : api.updateCustomerPo(po!.id, payload())), (saved) => {
      fill(saved);
      if (isNew) navigate(`${PO_ROUTE}/${saved.id}`, { replace: true });
    });
  }

  const setLine = (i: number, patch: Partial<LineForm>) => setLines(lines.map((l, j) => (j === i ? { ...l, ...patch } : l)));
  const pickItem = (i: number, productId: string) => {
    const it = itemById.get(productId);
    setLine(i, { productId, rate: lines[i].rate || String(it?.unitPrice ?? ''), taxRate: lines[i].taxRate || String(it?.taxRate ?? 0) });
  };
  const open = po?.status === 'Confirmed';
  const canProduce = open && can('work-orders', 'write');
  const canInvoice = open && can('sales-invoices', 'write') && po!.lines.some((l) => l.toInvoice > 0);

  return (
    <>
      <p className="crumbs"><Link to={PO_ROUTE}>Customer P.O.</Link> › {isNew ? 'New' : po?.number ?? '…'}</p>
      <div className="page-head">
        <h1>{isNew ? 'New customer P.O.' : po?.number ?? 'Customer P.O.'} {po && <DocPill status={po.status} />}</h1>
        {po && (
          <div className="row-actions">
            {po.status === 'Draft' && can(PAGE, 'update') && (
              <button disabled={busy} onClick={() => act('Confirmed. Work orders and invoices can now be raised.', () => api.confirmCustomerPo(po.id), fill)}>Confirm P.O.</button>
            )}
            {po.status === 'Draft' && can(PAGE, 'delete') && (
              <button className="link danger" disabled={busy} onClick={() => confirm(`Delete draft ${po.number}?`)
                && act('Deleted.', () => api.deleteCustomerPo(po.id), () => navigate(PO_ROUTE))}>Delete draft</button>
            )}
            {canInvoice && <Link className="button" to={`${INVOICE_ROUTE}/new?poId=${po.id}`}>Create invoice</Link>}
            {po.status === 'Confirmed' && can(PAGE, 'delete') && (
              <button className="link danger" disabled={busy} onClick={() => confirm(`Cancel ${po.number}?`)
                && act('Cancelled.', () => api.cancelCustomerPo(po.id), fill)}>Cancel P.O.</button>
            )}
          </div>
        )}
      </div>
      {(error || lookupError) && <p role="alert" className="error">{error ?? lookupError}</p>}
      {notice && <p role="status" className="ok">{notice}</p>}

      <form className="card" onSubmit={save} aria-label="Customer P.O.">
        <fieldset disabled={!editable || busy} className="form-grid">
          <label>Customer
            <select aria-label="Customer" required value={form.customerId} onChange={(e) => setForm({ ...form, customerId: e.target.value })}>
              <option value="">Choose…</option>
              {customers.map((c) => <option key={c.id} value={c.id}>{c.name}{c.city ? ` · ${c.city}` : ''}</option>)}
              {po && !customers.some((c) => c.id === po.customerId) && <option value={po.customerId}>{po.customerName}</option>}
            </select>
          </label>
          <label>Customer P.O. no.<input required maxLength={50} value={form.customerPoNo}
            onChange={(e) => setForm({ ...form, customerPoNo: e.target.value })} /></label>
          <label>P.O. date<input type="date" required max={today()} value={form.poDate} onChange={(e) => setForm({ ...form, poDate: e.target.value })} /></label>
          <label>Delivery date<input type="date" min={form.poDate} value={form.deliveryDate}
            onChange={(e) => setForm({ ...form, deliveryDate: e.target.value })} /></label>
        </fieldset>

        <div className="table-wrap">
          <table aria-label="Items" className="lines">
            <thead>
              <tr><th>#</th><th>Item</th><th className="num">Quantity</th><th className="num">Rate</th><th className="num">GST %</th>
                <th className="num">Amount</th>
                {po && po.status !== 'Draft' && <><th className="num">Produced</th><th className="num">Invoiced</th><th className="num">In stock</th></>}
                {(editable || canProduce) && <th />}</tr>
            </thead>
            <tbody>
              {lines.map((l, i) => {
                const saved = po?.status !== 'Draft' ? po?.lines[i] : undefined;
                const item = itemById.get(l.productId);
                return (
                  <tr key={i}>
                    <td>{i + 1}</td>
                    <td>
                      {editable ? (
                        <select aria-label={`Item ${i + 1}`} required value={l.productId} onChange={(e) => pickItem(i, e.target.value)}>
                          <option value="">Choose an item…</option>
                          {items.map((it) => <option key={it.id} value={it.id}>{it.sku} · {it.name}</option>)}
                        </select>
                      ) : <>{saved?.sku ?? item?.sku} · {saved?.productName ?? item?.name}</>}
                      {item && editable && <div className="hint">In stock: {num.format(item.stockQuantity)} {item.uom ?? ''}</div>}
                    </td>
                    <td className="num">{editable ? <input aria-label={`Quantity ${i + 1}`} type="number" min={1} step={1} required value={l.quantity}
                      onChange={(e) => setLine(i, { quantity: e.target.value })} /> : num.format(Number(l.quantity))}</td>
                    <td className="num">{editable ? <input aria-label={`Rate ${i + 1}`} type="number" min={0} step="0.01" required value={l.rate}
                      onChange={(e) => setLine(i, { rate: e.target.value })} /> : inr.format(Number(l.rate))}</td>
                    <td className="num">{editable ? <input aria-label={`GST ${i + 1}`} type="number" min={0} max={100} step="0.01" value={l.taxRate}
                      onChange={(e) => setLine(i, { taxRate: e.target.value })} /> : l.taxRate}</td>
                    <td className="num">{inr.format(lineTotals[i].amount)}</td>
                    {saved && <>
                      <td className="num">{num.format(saved.producedQty)}</td>
                      <td className="num">{num.format(saved.invoicedQty)}</td>
                      <td className="num">{num.format(saved.inStock)}</td>
                    </>}
                    {editable && (
                      <td><button type="button" className="link danger" aria-label={`Remove line ${i + 1}`} disabled={lines.length === 1}
                        onClick={() => setLines(lines.filter((_, j) => j !== i))}>Remove</button></td>
                    )}
                    {!editable && canProduce && (
                      <td>{saved && saved.toProduce > 0
                        ? <Link to={`${WO_ROUTE}/new?poId=${po!.id}&lineId=${saved.id}`} aria-label={`Produce line ${i + 1}`}>Produce {saved.toProduce}</Link>
                        : <span className="hint">Produced</span>}</td>
                    )}
                  </tr>
                );
              })}
            </tbody>
            <tfoot>
              <tr><td colSpan={5}>Sub-total</td><td className="num">{inr.format(po && !editable ? po.subTotal : sub)}</td></tr>
              <tr><td colSpan={5}>GST</td><td className="num">{inr.format(po && !editable ? po.taxTotal : tax)}</td></tr>
              <tr><td colSpan={5}>Total</td><td className="num">{inr.format(po && !editable ? po.grandTotal : sub + tax)}</td></tr>
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
            <span className="hint">Confirm the P.O. to start production and invoicing.</span>
          </div>
        )}
      </form>

      {po && po.status !== 'Draft' && (
        <section className="card" aria-label="Linked documents">
          <h2>Work orders</h2>
          {po.workOrders.length === 0 ? <p className="hint">None yet.</p> : (
            <ul className="doc-links">{po.workOrders.map((w) => (
              <li key={w.id}><Link to={`${WO_ROUTE}/${w.id}`}>{w.number}</Link> {w.detail} · {fmtDate(w.date)} <DocPill status={w.status} /></li>
            ))}</ul>
          )}
          <h2>Invoices</h2>
          {po.invoices.length === 0 ? <p className="hint">None yet.</p> : (
            <ul className="doc-links">{po.invoices.map((inv) => (
              <li key={inv.id}><Link to={`${INVOICE_ROUTE}/${inv.id}`}>{inv.number}</Link> {inr.format(inv.amount)} · {fmtDate(inv.date)} <DocPill status={inv.status} /></li>
            ))}</ul>
          )}
        </section>
      )}
    </>
  );
}
