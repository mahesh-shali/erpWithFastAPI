import { useCallback, useEffect, useMemo, useState, type FormEvent } from 'react';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom';
import {
  api, type CustomerPo, type DocumentLink, type InvoicePrint, type PagedResult, type SalesInvoice, type SalesInvoiceSummary,
  type UpsertSalesInvoice,
} from '../../api/client';
import { usePermissions } from '../../auth/PermissionsContext';
import { PrintFormatPicker, useRequestedFormat } from '../../components/PrintFormatPicker';
import { TemplateView } from '../../components/TemplateView';
import { errorText, fmtDate, inr, num, today } from '../inventory/format';
import { DocPill, INVOICE_ROUTE, PO_ROUTE, Pager, useDocAction, useSalesLookups } from './shared';

const PAGE = 'sales-invoices';
const statusLabel = (s: string) => (s === 'Submitted' ? 'Issued' : s);
const r2 = (n: number) => Math.round((n + Number.EPSILON) * 100) / 100;

/** Same maths as the API (Money.Line): discount first, GST on the taxable value, each rounded to paise. */
export function lineMaths(qty: number, rateValue: number, discount: number, taxRate: number) {
  const gross = r2(qty * rateValue);
  const amount = r2(gross * (100 - discount) / 100);
  const tax = r2(amount * taxRate / 100);
  return { gross, discount: r2(gross - amount), amount, tax, total: r2(amount + tax) };
}

/** Sales › Invoices › Sales Invoices. */
export function SalesInvoicesPage() {
  const { can } = usePermissions();
  const [f, setF] = useState({ status: '', search: '', page: 1 });
  const [search, setSearch] = useState('');
  const [data, setData] = useState<PagedResult<SalesInvoiceSummary> | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => { const t = setTimeout(() => setF((x) => (x.search === search ? x : { ...x, search, page: 1 })), 300); return () => clearTimeout(t); }, [search]);
  useEffect(() => {
    let alive = true;
    api.salesInvoices(f).then((d) => alive && setData(d)).catch((e) => alive && setError(errorText(e)));
    return () => { alive = false; };
  }, [f]);

  return (
    <>
      <div className="page-head">
        <h1>Sales invoices</h1>
        {can(PAGE, 'write') && <Link className="button" to={`${INVOICE_ROUTE}/new`}>New invoice</Link>}
      </div>
      {error && <p role="alert" className="error">{error}</p>}
      <div className="card toolbar">
        <label>Status
          <select aria-label="Status" value={f.status} onChange={(e) => setF({ ...f, status: e.target.value, page: 1 })}>
            <option value="">All</option><option value="Draft">Draft</option><option value="Submitted">Issued</option><option>Cancelled</option>
          </select>
        </label>
        <label className="grow">Search<input type="search" placeholder="Invoice, customer or P.O." value={search} onChange={(e) => setSearch(e.target.value)} /></label>
      </div>
      <div className="card table-wrap">
        <table aria-label="Sales invoices">
          <thead>
            <tr><th>Number</th><th>Date</th><th>Due</th><th>Customer</th><th>P.O.</th><th className="num">Items</th><th className="num">Total</th><th>Status</th><th /></tr>
          </thead>
          <tbody>
            {data?.items.map((i) => (
              <tr key={i.id}>
                <td><Link to={`${INVOICE_ROUTE}/${i.id}`}>{i.number}</Link></td>
                <td>{fmtDate(i.invoiceDate)}</td><td>{fmtDate(i.dueDate)}</td><td>{i.customerName}</td><td>{i.customerPoNumber ?? '—'}</td>
                <td className="num">{i.lineCount}</td><td className="num">{inr.format(i.grandTotal)}</td>
                <td><DocPill status={i.status} label={statusLabel(i.status)} /></td>
                <td><Link to={`${INVOICE_ROUTE}/${i.id}/print`}>Print</Link></td>
              </tr>
            ))}
          </tbody>
        </table>
        {data && data.items.length === 0 && <p className="hint">No invoices yet.</p>}
        {data && <Pager page={f.page} total={data.total} pageSize={data.pageSize} onPage={(page) => setF({ ...f, page })} />}
      </div>
    </>
  );
}

interface LineForm { poLineId: string; productId: string; quantity: string; rate: string; discount: string; taxRate: string }

const fromPo = (po: CustomerPo): LineForm[] => po.lines.filter((l) => l.toInvoice > 0).map((l) => ({
  poLineId: l.id, productId: l.productId, quantity: String(l.toInvoice), rate: String(l.rate), discount: '0', taxRate: String(l.taxRate),
}));

/** /sales/invoices/new[?poId=] and /:id — draft, issue (stock goes out), cancel, print. */
export function SalesInvoicePage() {
  const { id } = useParams();
  const isNew = !id || id === 'new';
  const [query] = useSearchParams();
  const navigate = useNavigate();
  const { can } = usePermissions();
  const { customers, items, stores, lookupError } = useSalesLookups({ customers: true, items: true, stores: true });
  const [inv, setInv] = useState<SalesInvoice | null>(null);
  const [po, setPo] = useState<CustomerPo | null>(null);
  const [openPos, setOpenPos] = useState<DocumentLink[]>([]);
  const [form, setForm] = useState({
    customerId: '', poId: query.get('poId') ?? '', invoiceDate: today(), dueDate: '', storeId: '', shippingAddress: '', remarks: '',
  });
  const [lines, setLines] = useState<LineForm[]>([]);
  const { busy, error, setError, notice, act } = useDocAction();

  const fill = useCallback((i: SalesInvoice) => {
    setInv(i);
    setForm({ customerId: i.customerId, poId: i.customerPoId ?? '', invoiceDate: i.invoiceDate, dueDate: i.dueDate, storeId: i.storeId,
      shippingAddress: i.shippingAddress ?? '', remarks: i.remarks ?? '' });
    setLines(i.lines.map((l) => ({ poLineId: l.customerPoLineId ?? '', productId: l.productId, quantity: String(l.quantity),
      rate: String(l.rate), discount: String(l.discountPercent), taxRate: String(l.taxRate) })));
  }, []);
  useEffect(() => { if (!isNew) api.salesInvoice(id!).then(fill).catch((e) => setError(errorText(e))); }, [id, isNew, fill, setError]);
  useEffect(() => {
    const main = stores.find((s) => s.isDefault)?.id ?? '';
    if (isNew && main) setForm((f) => ({ ...f, storeId: f.storeId || main }));
  }, [stores, isNew]);
  useEffect(() => {
    if (isNew) api.lookupOpenPos(form.customerId).then(setOpenPos).catch(() => undefined);
  }, [isNew, form.customerId]);

  // Invoicing a P.O.: its customer, and (on a new invoice) its lines still to be invoiced.
  useEffect(() => {
    if (!form.poId) { setPo(null); return; }
    let alive = true;
    api.customerPo(form.poId).then((p) => {
      if (!alive) return;
      setPo(p);
      if (isNew) {
        setForm((f) => ({ ...f, customerId: p.customerId }));
        setLines(fromPo(p));
      }
    }).catch((e) => alive && setError(errorText(e)));
    return () => { alive = false; };
  }, [form.poId, isNew, setError]);
  useEffect(() => { if (isNew && !form.poId && lines.length === 0) setLines([{ poLineId: '', productId: '', quantity: '1', rate: '', discount: '0', taxRate: '' }]); },
    [isNew, form.poId, lines.length]);

  const editable = isNew ? can(PAGE, 'write') : inv?.status === 'Draft' && can(PAGE, 'update');
  const itemById = useMemo(() => new Map(items.map((i) => [i.id, i])), [items]);
  const poLineById = useMemo(() => new Map((po?.lines ?? []).map((l) => [l.id, l])), [po]);
  const maths = lines.map((l) => lineMaths(Number(l.quantity) || 0, Number(l.rate) || 0, Number(l.discount) || 0, Number(l.taxRate) || 0));
  const totals = maths.reduce((t, m) => ({ gross: t.gross + m.gross, discount: t.discount + m.discount, tax: t.tax + m.tax }), { gross: 0, discount: 0, tax: 0 });
  const exact = r2(totals.gross - totals.discount + totals.tax);
  const grand = Math.round(exact);
  const customer = customers.find((c) => c.id === form.customerId);

  const payload = (): UpsertSalesInvoice => ({
    customerId: form.customerId || null, customerPoId: form.poId || null, invoiceDate: form.invoiceDate || null,
    dueDate: form.dueDate || null, storeId: form.storeId, shippingAddress: form.shippingAddress.trim() || null, remarks: form.remarks.trim() || null,
    lines: lines.filter((l) => l.productId).map((l) => ({
      customerPoLineId: l.poLineId || null, productId: l.productId, quantity: Number(l.quantity), rate: Number(l.rate),
      discountPercent: Number(l.discount) || 0, taxRate: l.taxRate === '' ? null : Number(l.taxRate),
    })),
  });

  async function save(e: FormEvent) {
    e.preventDefault();
    await act('Draft saved.', () => (isNew ? api.createSalesInvoice(payload()) : api.updateSalesInvoice(inv!.id, payload())), (saved) => {
      fill(saved);
      if (isNew) navigate(`${INVOICE_ROUTE}/${saved.id}`, { replace: true });
    });
  }

  const setLine = (i: number, patch: Partial<LineForm>) => setLines(lines.map((l, j) => (j === i ? { ...l, ...patch } : l)));
  const pickItem = (i: number, productId: string) => {
    const it = itemById.get(productId);
    setLine(i, { productId, rate: lines[i].rate || String(it?.unitPrice ?? ''), taxRate: lines[i].taxRate || String(it?.taxRate ?? 0) });
  };
  const fromAPo = Boolean(form.poId);

  return (
    <>
      <p className="crumbs"><Link to={INVOICE_ROUTE}>Sales invoices</Link> › {isNew ? 'New' : inv?.number ?? '…'}</p>
      <div className="page-head">
        <h1>{isNew ? 'New invoice' : inv?.number ?? 'Invoice'} {inv && <DocPill status={inv.status} label={statusLabel(inv.status)} />}</h1>
        {inv && (
          <div className="row-actions">
            <Link className="button" to={`${INVOICE_ROUTE}/${inv.id}/print`}>Print</Link>
            {inv.status === 'Draft' && can(PAGE, 'update') && (
              <button disabled={busy} onClick={() => act('Invoice issued. The items have left the store.', () => api.submitSalesInvoice(inv.id), fill)}>Issue invoice</button>
            )}
            {inv.status === 'Draft' && can(PAGE, 'delete') && (
              <button className="link danger" disabled={busy} onClick={() => confirm(`Delete draft ${inv.number}?`)
                && act('Deleted.', () => api.deleteSalesInvoice(inv.id), () => navigate(INVOICE_ROUTE))}>Delete draft</button>
            )}
            {inv.status === 'Submitted' && can(PAGE, 'delete') && (
              <button className="link danger" disabled={busy} onClick={() => confirm(`Cancel ${inv.number}? The items go back into stock.`)
                && act('Cancelled. The stock was returned.', () => api.cancelSalesInvoice(inv.id), fill)}>Cancel invoice</button>
            )}
          </div>
        )}
      </div>
      {(error || lookupError) && <p role="alert" className="error">{error ?? lookupError}</p>}
      {notice && <p role="status" className="ok">{notice}</p>}

      <form className="card" onSubmit={save} aria-label="Sales invoice">
        <fieldset disabled={!editable || busy} className="form-grid">
          <label>Customer
            <select aria-label="Customer" required value={form.customerId} disabled={fromAPo}
              onChange={(e) => setForm({ ...form, customerId: e.target.value })}>
              <option value="">Choose…</option>
              {customers.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
              {inv && !customers.some((c) => c.id === inv.customerId) && <option value={inv.customerId}>{inv.customerName}</option>}
            </select>
          </label>
          <label>Against P.O.
            <select aria-label="Against P.O." value={form.poId} disabled={!isNew}
              onChange={(e) => { setForm({ ...form, poId: e.target.value }); if (!e.target.value) setLines([]); }}>
              <option value="">— None —</option>
              {openPos.map((p) => <option key={p.id} value={p.id}>{p.number} · {p.detail}</option>)}
              {form.poId && !openPos.some((p) => p.id === form.poId) && (po || inv) &&
                <option value={form.poId}>{po?.number ?? inv?.customerPoNumber}</option>}
            </select>
          </label>
          <label>Invoice date<input type="date" required max={today()} value={form.invoiceDate} onChange={(e) => setForm({ ...form, invoiceDate: e.target.value })} /></label>
          <label>Due date<input type="date" min={form.invoiceDate} value={form.dueDate} onChange={(e) => setForm({ ...form, dueDate: e.target.value })} /></label>
          <label>Issue from store
            <select aria-label="Issue from store" required value={form.storeId} onChange={(e) => setForm({ ...form, storeId: e.target.value })}>
              <option value="">Choose…</option>{stores.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
            </select>
          </label>
          <label className="span-all">Ship to (if not the billing address)<textarea rows={2} maxLength={800} value={form.shippingAddress}
            onChange={(e) => setForm({ ...form, shippingAddress: e.target.value })} /></label>
          {isNew && !form.dueDate && customer && <p className="hint span-all">Due date: {customer.paymentTermsDays ?? 0} days after the invoice date (customer’s payment terms).</p>}
          {po && <p className="hint span-all">P.O. <Link to={`${PO_ROUTE}/${po.id}`}>{po.number}</Link> · customer ref. {po.customerPoNo}</p>}
        </fieldset>

        <div className="table-wrap">
          <table aria-label="Items" className="lines">
            <thead>
              <tr><th>#</th><th>Item</th><th className="num">Quantity</th><th className="num">Rate</th><th className="num">Disc. %</th>
                <th className="num">GST %</th><th className="num">Taxable</th><th className="num">GST</th><th className="num">Total</th>{editable && <th />}</tr>
            </thead>
            <tbody>
              {lines.map((l, i) => {
                const saved = inv && inv.status !== 'Draft' ? inv.lines[i] : undefined;
                const item = itemById.get(l.productId);
                const pl = poLineById.get(l.poLineId);
                const m = maths[i];
                return (
                  <tr key={i}>
                    <td>{i + 1}</td>
                    <td>
                      {editable && !fromAPo ? (
                        <select aria-label={`Item ${i + 1}`} required value={l.productId} onChange={(e) => pickItem(i, e.target.value)}>
                          <option value="">Choose an item…</option>
                          {items.map((it) => <option key={it.id} value={it.id}>{it.sku} · {it.name}</option>)}
                        </select>
                      ) : <>{saved?.sku ?? pl?.sku ?? item?.sku ?? inv?.lines[i]?.sku} · {saved?.productName ?? pl?.productName ?? item?.name ?? inv?.lines[i]?.productName}</>}
                      {editable && pl && <div className="hint">{pl.toInvoice} left to invoice · {pl.inStock} in stock</div>}
                      {editable && !pl && item && <div className="hint">In stock: {num.format(item.stockQuantity)} {item.uom ?? ''}</div>}
                    </td>
                    <td className="num">{editable ? <input aria-label={`Quantity ${i + 1}`} type="number" min={1} max={pl?.toInvoice} step={1} required value={l.quantity}
                      onChange={(e) => setLine(i, { quantity: e.target.value })} /> : num.format(Number(l.quantity))}</td>
                    <td className="num">{editable ? <input aria-label={`Rate ${i + 1}`} type="number" min={0} step="0.01" required value={l.rate}
                      onChange={(e) => setLine(i, { rate: e.target.value })} /> : inr.format(Number(l.rate))}</td>
                    <td className="num">{editable ? <input aria-label={`Discount ${i + 1}`} type="number" min={0} max={100} step="0.01" value={l.discount}
                      onChange={(e) => setLine(i, { discount: e.target.value })} /> : l.discount}</td>
                    <td className="num">{editable ? <input aria-label={`GST ${i + 1}`} type="number" min={0} max={100} step="0.01" value={l.taxRate}
                      onChange={(e) => setLine(i, { taxRate: e.target.value })} /> : l.taxRate}</td>
                    <td className="num">{inr.format(m.amount)}</td><td className="num">{inr.format(m.tax)}</td><td className="num">{inr.format(m.total)}</td>
                    {editable && (
                      <td><button type="button" className="link danger" aria-label={`Remove line ${i + 1}`} disabled={lines.length === 1}
                        onClick={() => setLines(lines.filter((_, j) => j !== i))}>Remove</button></td>
                    )}
                  </tr>
                );
              })}
              {fromAPo && lines.length === 0 && <tr><td colSpan={10} className="hint">Everything on this P.O. has been invoiced.</td></tr>}
            </tbody>
            <tfoot>
              <tr><td colSpan={8}>Sub-total</td><td className="num">{inr.format(inv && !editable ? inv.subTotal : totals.gross)}</td></tr>
              <tr><td colSpan={8}>Discount</td><td className="num">−{inr.format(inv && !editable ? inv.discountTotal : totals.discount)}</td></tr>
              <tr><td colSpan={8}>GST</td><td className="num">{inr.format(inv && !editable ? inv.taxTotal : totals.tax)}</td></tr>
              <tr><td colSpan={8}>Round off</td><td className="num">{inr.format(inv && !editable ? inv.roundOff : r2(grand - exact))}</td></tr>
              <tr><td colSpan={8}>Grand total</td><td className="num">{inr.format(inv && !editable ? inv.grandTotal : grand)}</td></tr>
            </tfoot>
          </table>
        </div>
        {editable && !fromAPo && (
          <button type="button" className="link" onClick={() => setLines([...lines, { poLineId: '', productId: '', quantity: '1', rate: '', discount: '0', taxRate: '' }])}>+ Add item</button>
        )}
        {inv && <p className="hint">{inv.amountInWords}{inv.status === 'Submitted' && ` · Cost of goods ${inr.format(inv.costOfGoods)}`}</p>}

        <fieldset disabled={!editable || busy} className="form-grid">
          <label className="span-all">Remarks<input maxLength={500} value={form.remarks} onChange={(e) => setForm({ ...form, remarks: e.target.value })} /></label>
        </fieldset>
        {editable && (
          <div className="actions">
            <button disabled={busy || lines.length === 0}>{busy ? 'Saving…' : 'Save draft'}</button>
            <span className="hint">Issuing the invoice takes the items out of the store.</span>
          </div>
        )}
      </form>
    </>
  );
}

/**
 * /sales/invoices/:id/print — a clean A4 tax invoice; the browser's print dialog makes the paper or PDF copy.
 * Prints with the format the invoice was issued with (drafts: the default); the picker switches to another format.
 */
export function InvoicePrintPage() {
  const { id } = useParams();
  const [format, setFormat] = useRequestedFormat();
  const [data, setData] = useState<InvoicePrint | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { api.invoicePrint(id!, format).then(setData).catch((e) => setError(errorText(e))); }, [id, format]);
  useEffect(() => { if (data) document.title = `${data.invoice.number} · ${data.invoice.billingName}`; }, [data]);

  if (error) return <p role="alert" className="error">{error}</p>;
  if (!data) return <p className="hint">Loading…</p>;
  const { company: co, invoice: inv, template } = data;
  const half = (n: number) => r2(n / 2);
  const toolbar = (
    <div className="print-toolbar no-print">
      <Link to={`${INVOICE_ROUTE}/${inv.id}`}>‹ Back to {inv.number}</Link>
      <PrintFormatPicker formats={data.formats} selected={template?.templateId ?? null} onChange={setFormat} />
      <span className="hint">{template ? `Layout: ${template.name} (version ${template.version})` : 'Built-in layout'}</span>
      <button onClick={() => window.print()}>Print</button>
    </div>
  );
  const warning = data.templateWarning && <p role="alert" className="error no-print print-warning">{data.templateWarning}</p>;
  if (template) {
    // The organization's own layout (Settings › Print templates), rendered and sanitized by the API.
    return (
      <div className="print-page">
        {toolbar}
        <article className="invoice invoice-custom" aria-label="Tax invoice">
          <TemplateView html={template.html} css={template.css} label={`${inv.number} (${template.name})`} />
        </article>
      </div>
    );
  }
  return (
    <div className="print-page">
      {toolbar}
      {warning}
      <article className="invoice" aria-label="Tax invoice">
        {inv.status !== 'Submitted' && <div className="watermark" aria-hidden>{inv.status === 'Draft' ? 'DRAFT' : 'CANCELLED'}</div>}
        <header className="invoice-head">
          <div>
            <h1>{co.name}</h1>
            {co.address && <p className="pre">{co.address}</p>}
            <p>{[co.state, co.phone, co.email].filter(Boolean).join(' · ')}</p>
            {co.taxId && <p><strong>GSTIN:</strong> {co.taxId}</p>}
          </div>
          <div className="invoice-title">
            <h2>Tax Invoice</h2>
            <dl>
              <dt>Invoice no.</dt><dd>{inv.number}</dd>
              <dt>Date</dt><dd>{fmtDate(inv.invoiceDate)}</dd>
              <dt>Due date</dt><dd>{fmtDate(inv.dueDate)}</dd>
              {inv.customerPoRef && <><dt>Your P.O.</dt><dd>{inv.customerPoRef}</dd></>}
              {inv.customerPoNumber && <><dt>Our ref.</dt><dd>{inv.customerPoNumber}</dd></>}
            </dl>
          </div>
        </header>
        <section className="invoice-parties">
          <div>
            <h3>Bill to</h3>
            <p><strong>{inv.billingName}</strong></p>
            {inv.billingAddress && <p className="pre">{inv.billingAddress}</p>}
            {inv.billingTaxId && <p>GSTIN: {inv.billingTaxId}</p>}
          </div>
          <div>
            <h3>Ship to</h3>
            <p className="pre">{inv.shippingAddress ?? inv.billingAddress ?? inv.billingName}</p>
            {inv.billingState && <p>Place of supply: {inv.billingState}</p>}
          </div>
        </section>
        <table className="invoice-lines" aria-label="Invoice lines">
          <thead>
            <tr><th>#</th><th>Item</th><th>HSN</th><th className="num">Qty</th><th className="num">Rate</th><th className="num">Disc. %</th>
              <th className="num">Taxable</th><th className="num">GST %</th><th className="num">GST</th><th className="num">Amount</th></tr>
          </thead>
          <tbody>
            {inv.lines.map((l) => (
              <tr key={l.lineNo}>
                <td>{l.lineNo}</td><td>{l.productName}<div className="hint">{l.sku}</div></td><td>{l.hsnCode ?? '—'}</td>
                <td className="num">{num.format(l.quantity)} {l.uom ?? ''}</td><td className="num">{inr.format(l.rate)}</td>
                <td className="num">{l.discountPercent || '—'}</td><td className="num">{inr.format(l.amount)}</td>
                <td className="num">{l.taxRate}</td><td className="num">{inr.format(l.taxAmount)}</td><td className="num">{inr.format(l.total)}</td>
              </tr>
            ))}
          </tbody>
        </table>
        <section className="invoice-foot">
          <table className="tax-summary" aria-label="Tax summary">
            <thead>
              <tr><th className="num">GST %</th><th className="num">Taxable</th>
                {inv.interState ? <th className="num">IGST</th> : <><th className="num">CGST</th><th className="num">SGST</th></>}</tr>
            </thead>
            <tbody>
              {inv.taxSummary.map((t) => (
                <tr key={t.taxRate}>
                  <td className="num">{t.taxRate}</td><td className="num">{inr.format(t.taxable)}</td>
                  {inv.interState ? <td className="num">{inr.format(t.tax)}</td>
                    : <><td className="num">{inr.format(half(t.tax))}</td><td className="num">{inr.format(r2(t.tax - half(t.tax)))}</td></>}
                </tr>
              ))}
            </tbody>
          </table>
          <dl className="invoice-totals">
            <dt>Sub-total</dt><dd>{inr.format(inv.subTotal)}</dd>
            {inv.discountTotal > 0 && <><dt>Discount</dt><dd>−{inr.format(inv.discountTotal)}</dd></>}
            <dt>{inv.interState ? 'IGST' : 'CGST + SGST'}</dt><dd>{inr.format(inv.taxTotal)}</dd>
            {inv.roundOff !== 0 && <><dt>Round off</dt><dd>{inr.format(inv.roundOff)}</dd></>}
            <dt className="grand">Total</dt><dd className="grand">{inr.format(inv.grandTotal)}</dd>
          </dl>
        </section>
        <p className="words"><strong>Amount in words:</strong> {inv.amountInWords}</p>
        {inv.remarks && <p><strong>Remarks:</strong> {inv.remarks}</p>}
        <footer className="invoice-sign">
          <p className="hint">This is a computer-generated invoice.</p>
          <div><p>For {co.name}</p><p className="sign-line">Authorised signatory</p></div>
        </footer>
      </article>
    </div>
  );
}
