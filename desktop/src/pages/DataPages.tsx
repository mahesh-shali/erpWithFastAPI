import { useCallback, useEffect, useState, type FormEvent } from 'react';
import { Link } from 'react-router-dom';
import {
  api, ITEM_TYPES, type Customer, type ItemType, type Order, type PagedResult, type Product, type Uom, type UpsertCustomer,
  type UpsertProduct,
} from '../api/client';
import { Pager } from './sales/shared';
import { usePermissions } from '../auth/PermissionsContext';
import { ChatPanel } from '../components/ChatPanel';

const inr = new Intl.NumberFormat('en-IN', { style: 'currency', currency: 'INR', maximumFractionDigits: 2 });

function useList<T>(fetcher: () => Promise<T[]>) {
  const [items, setItems] = useState<T[]>([]);
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(async () => {
    try {
      setItems(await fetcher());
      setError(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load');
    }
  }, [fetcher]);
  useEffect(() => { void load(); }, [load]);
  return { items, error, setError, load };
}

/** One page of a server-paged list (search/filters live in the fetcher's closure); reloads when the fetcher changes. */
function usePagedList<T>(fetcher: (page: number) => Promise<PagedResult<T>>) {
  const [page, setPage] = useState(1);
  const [data, setData] = useState<PagedResult<T> | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { setPage(1); }, [fetcher]);
  const load = useCallback(async () => {
    try {
      setData(await fetcher(page));
      setError(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load');
    }
  }, [fetcher, page]);
  useEffect(() => { void load(); }, [load]);
  return { items: data?.items ?? [], data, page, setPage, error, setError, load };
}

/** Search text that settles 300 ms after the last keystroke. */
function useDebounced(value: string) {
  const [settled, setSettled] = useState(value);
  useEffect(() => { const t = setTimeout(() => setSettled(value), 300); return () => clearTimeout(t); }, [value]);
  return settled;
}

function ListFooter({ data, page, onPage, what }: {
  data: PagedResult<unknown> | null; page: number; onPage: (p: number) => void; what: string;
}) {
  if (!data) return null;
  const from = data.total === 0 ? 0 : (page - 1) * data.pageSize + 1;
  const to = Math.min(page * data.pageSize, data.total);
  return (
    <>
      <p className="hint">{data.total === 0 ? `No ${what} found.` : `Showing ${from}–${to} of ${data.total} ${what}.`}</p>
      <Pager page={page} total={data.total} pageSize={data.pageSize} onPage={onPage} />
    </>
  );
}

/** Master › Customers: billing details used on P.O.s and printed on invoices. Buttons follow the page rights. */
export function CustomersPage() {
  const { can } = usePermissions();
  const [search, setSearch] = useState('');
  const term = useDebounced(search.trim());
  const fetcher = useCallback((page: number) => api.customers(page, term), [term]);
  const { items, data, page, setPage, error, setError, load } = usePagedList<Customer>(fetcher);
  const [editing, setEditing] = useState<Customer | 'new' | null>(null);
  const remove = async (c: Customer) => {
    if (!confirm(`Delete ${c.name}?`)) return;
    try { await api.deleteCustomer(c.id); await load(); } catch (e) { setError((e as Error).message); }
  };
  const actions = can('customers', 'update') || can('customers', 'delete');
  return (
    <>
      <div className="page-head">
        <h1>Customers</h1>
        {can('customers', 'write') && editing === null && <button onClick={() => setEditing('new')}>Add customer</button>}
      </div>
      {error && <p role="alert" className="error">{error}</p>}
      {editing !== null && (
        <CustomerForm customer={editing === 'new' ? null : editing}
          onDone={async (saved, name) => {
            setEditing(null);
            // A new customer may sort beyond the first page: search for it so it shows straight away.
            if (saved && name && name !== search) setSearch(name); else if (saved) await load();
          }} />
      )}
      <div className="card toolbar">
        <label className="grow">Search<input type="search" placeholder="Customer name" value={search}
          onChange={(e) => setSearch(e.target.value)} /></label>
      </div>
      <div className="card table-wrap">
        <table aria-label="Customers">
          <thead>
            <tr><th>Name</th><th>Contact</th><th>Email</th><th>Phone</th><th>City</th><th>State</th><th>GSTIN</th><th>Country</th>
              {actions && <th />}</tr>
          </thead>
          <tbody>
            {items.map((c) => (
              <tr key={c.id}>
                <td>{c.name}</td><td>{c.contactPerson ?? '—'}</td><td>{c.email ?? '—'}</td><td>{c.phone ?? '—'}</td>
                <td>{c.city ?? '—'}</td><td>{c.state ?? '—'}</td><td>{c.taxId ?? '—'}</td><td>{c.country ?? '—'}</td>
                {actions && (
                  <td className="row-actions">
                    {can('customers', 'update') && <button className="link" onClick={() => setEditing(c)}>Edit</button>}
                    {can('customers', 'delete') && <button className="link danger" onClick={() => remove(c)}>Delete</button>}
                  </td>
                )}
              </tr>
            ))}
          </tbody>
        </table>
        <ListFooter data={data} page={page} onPage={setPage} what="customers" />
      </div>
    </>
  );
}

const blank = (v: string) => (v.trim() ? v.trim() : null);

function CustomerForm({ customer, onDone }: { customer: Customer | null; onDone: (saved: boolean, name?: string) => Promise<void> }) {
  const [form, setForm] = useState({
    name: customer?.name ?? '', contactPerson: customer?.contactPerson ?? '', email: customer?.email ?? '', phone: customer?.phone ?? '',
    taxId: customer?.taxId ?? '', address: customer?.address ?? '', city: customer?.city ?? '', state: customer?.state ?? '',
    postalCode: customer?.postalCode ?? '', country: customer?.country ?? 'India', paymentTermsDays: String(customer?.paymentTermsDays ?? 0),
  });
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const set = (k: keyof typeof form) => (e: { target: { value: string } }) => setForm({ ...form, [k]: e.target.value });

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    const body: UpsertCustomer = {
      name: form.name.trim(), contactPerson: blank(form.contactPerson), email: blank(form.email), phone: blank(form.phone),
      taxId: blank(form.taxId), address: blank(form.address), city: blank(form.city), state: blank(form.state),
      postalCode: blank(form.postalCode), country: blank(form.country), paymentTermsDays: Number(form.paymentTermsDays) || 0,
    };
    try {
      const saved = await api.saveCustomer(body, customer?.id);
      await onDone(true, customer ? undefined : saved.name);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not save');
      setBusy(false);
    }
  }

  return (
    <form className="card form-grid" onSubmit={submit} aria-label={customer ? 'Edit customer' : 'Add customer'}>
      <h2>{customer ? `Edit ${customer.name}` : 'Add customer'}</h2>
      <label>Name<input required maxLength={200} value={form.name} onChange={set('name')} /></label>
      <label>Contact person<input maxLength={100} value={form.contactPerson} onChange={set('contactPerson')} /></label>
      <label>Email<input type="email" maxLength={320} value={form.email} onChange={set('email')} /></label>
      <label>Phone<input maxLength={30} value={form.phone} onChange={set('phone')} /></label>
      <label>GSTIN<input maxLength={20} value={form.taxId} onChange={set('taxId')} /></label>
      <label>Payment terms (days)<input type="number" min={0} max={365} value={form.paymentTermsDays} onChange={set('paymentTermsDays')} /></label>
      <label className="span-all">Address<textarea rows={2} maxLength={500} value={form.address} onChange={set('address')} /></label>
      <label>City<input maxLength={100} value={form.city} onChange={set('city')} /></label>
      <label>State<input maxLength={100} value={form.state} onChange={set('state')} /></label>
      <label>PIN code<input maxLength={20} value={form.postalCode} onChange={set('postalCode')} /></label>
      <label>Country<input maxLength={100} value={form.country} onChange={set('country')} /></label>
      <p className="hint span-all">Invoices copy the name, address, state and GSTIN when they are issued. The state decides CGST + SGST or IGST.</p>
      <div className="actions">
        <button disabled={busy}>{busy ? 'Saving…' : 'Save'}</button>
        <button type="button" className="link" onClick={() => onDone(false)}>Cancel</button>
        {error && <span role="alert" className="error">{error}</span>}
      </div>
    </form>
  );
}

/** Master › Inventory › Items: every detail of an item (type, HSN, GST, costs, reorder level, drawing). Page key "products". */
export function ProductsPage() {
  const { can } = usePermissions();
  const [itemType, setItemType] = useState('');
  const [search, setSearch] = useState('');
  const term = useDebounced(search.trim());
  const fetcher = useCallback((page: number) => api.products(page, { search: term, itemType }), [term, itemType]);
  const { items, data, page, setPage, error, setError, load } = usePagedList<Product>(fetcher);
  const [editing, setEditing] = useState<Product | 'new' | null>(null);
  const remove = async (p: Product) => {
    if (!confirm(`Delete ${p.name}?`)) return;
    try { await api.deleteProduct(p.id); await load(); } catch (e) { setError((e as Error).message); }
  };
  const actions = can('products', 'update') || can('products', 'delete') || can('stock-details', 'read');
  return (
    <>
      <div className="page-head">
        <h1>Items</h1>
        {can('products', 'write') && editing === null && <button onClick={() => setEditing('new')}>Add item</button>}
      </div>
      {error && <p role="alert" className="error">{error}</p>}
      {editing !== null && (
        <ProductForm product={editing === 'new' ? null : editing}
          onDone={async (saved) => { setEditing(null); if (saved) await load(); }} />
      )}
      <div className="card toolbar">
        <label className="grow">Search<input type="search" placeholder="Name, SKU, HSN or drawing no." value={search}
          onChange={(e) => setSearch(e.target.value)} /></label>
        <label>Item type
          <select aria-label="Item type filter" value={itemType} onChange={(e) => setItemType(e.target.value)}>
            <option value="">All types</option>{ITEM_TYPES.map((t) => <option key={t}>{t}</option>)}
          </select>
        </label>
      </div>
      <div className="card table-wrap">
        <table aria-label="Items">
          <thead>
            <tr><th>SKU</th><th>Name</th><th>Type</th><th>Category</th><th>HSN</th><th>Unit</th><th className="num">Price</th>
              <th className="num">GST %</th><th className="num">Stock</th><th className="num">Reorder at</th>{actions && <th />}</tr>
          </thead>
          <tbody>
            {items.map((p) => (
              <tr key={p.id} className={p.isActive === false ? 'reversal' : undefined}>
                <td>{p.sku}</td>
                <td>{p.name}{p.isActive === false && <span className="tag">Inactive</span>}</td>
                <td>{p.itemType ?? 'Finished Good'}</td><td>{p.category ?? '—'}</td><td>{p.hsnCode ?? '—'}</td><td>{p.uom ?? '—'}</td>
                <td className="num">{inr.format(p.unitPrice)}</td><td className="num">{p.taxRate ?? 0}</td>
                <td className="num">{p.stockQuantity}
                  {(p.reorderLevel ?? 0) > 0 && p.stockQuantity < (p.reorderLevel ?? 0) && <span className="tag warn">Low</span>}</td>
                <td className="num">{p.reorderLevel ?? 0}</td>
                {actions && (
                  <td className="row-actions">
                    {can('stock-details', 'read') && <Link to={`/inventory/stock-details/${p.id}`}>Stock</Link>}
                    {can('products', 'update') && <button className="link" onClick={() => setEditing(p)}>Edit</button>}
                    {can('products', 'delete') && <button className="link danger" onClick={() => remove(p)}>Delete</button>}
                  </td>
                )}
              </tr>
            ))}
          </tbody>
        </table>
        <ListFooter data={data} page={page} onPage={setPage} what="items" />
        <p className="hint">Stock is the total over all stores; see Inventory › Stock Position per store.</p>
      </div>
    </>
  );
}

/** Opening stock (new item) goes into the default store; a changed stock figure is posted there as an adjustment. */
function ProductForm({ product, onDone }: { product: Product | null; onDone: (saved: boolean) => Promise<void> }) {
  const [uoms, setUoms] = useState<Uom[]>([]);
  const [form, setForm] = useState({
    sku: product?.sku ?? '', name: product?.name ?? '', category: product?.category ?? '',
    unitPrice: String(product?.unitPrice ?? ''), stockQuantity: String(product?.stockQuantity ?? 0), uomId: product?.uomId ?? '',
    itemType: (product?.itemType ?? 'Finished Good') as ItemType, description: product?.description ?? '', hsnCode: product?.hsnCode ?? '',
    taxRate: String(product?.taxRate ?? 18), standardCost: String(product?.standardCost ?? 0), reorderLevel: String(product?.reorderLevel ?? 0),
    leadTimeDays: String(product?.leadTimeDays ?? 0), drawingNo: product?.drawingNo ?? '', isActive: product?.isActive ?? true,
  });
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  useEffect(() => { api.lookupUoms().then(setUoms).catch(() => undefined); }, []);
  const set = (k: keyof typeof form) => (e: { target: { value: string } }) => setForm({ ...form, [k]: e.target.value });

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    const body: UpsertProduct = {
      sku: form.sku.trim(), name: form.name.trim(), category: form.category.trim() || null,
      unitPrice: Number(form.unitPrice), stockQuantity: Number(form.stockQuantity), uomId: form.uomId || null,
      itemType: form.itemType, description: blank(form.description), hsnCode: blank(form.hsnCode), taxRate: Number(form.taxRate) || 0,
      standardCost: Number(form.standardCost) || 0, reorderLevel: Number(form.reorderLevel) || 0, leadTimeDays: Number(form.leadTimeDays) || 0,
      drawingNo: blank(form.drawingNo), isActive: form.isActive,
    };
    try {
      await (product ? api.updateProduct(product.id, body) : api.createProduct(body));
      await onDone(true);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not save');
      setBusy(false);
    }
  }

  return (
    <form className="card form-grid" onSubmit={submit} aria-label={product ? 'Edit item' : 'Add item'}>
      <h2>{product ? `Edit ${product.name}` : 'Add item'}</h2>
      <label>SKU<input required maxLength={50} value={form.sku} onChange={set('sku')} /></label>
      <label>Name<input required maxLength={200} value={form.name} onChange={set('name')} /></label>
      <label>Item type
        <select aria-label="Item type" value={form.itemType} onChange={set('itemType')}>
          {ITEM_TYPES.map((t) => <option key={t}>{t}</option>)}
        </select>
      </label>
      <label>Category<input maxLength={100} value={form.category} onChange={set('category')} /></label>
      <label>Unit
        <select aria-label="Unit" value={form.uomId} onChange={set('uomId')}>
          <option value="">—</option>{uoms.map((u) => <option key={u.id} value={u.id}>{u.code} · {u.name}</option>)}
        </select>
      </label>
      <label>Drawing / part no.<input maxLength={50} value={form.drawingNo} onChange={set('drawingNo')} /></label>
      <h2>Pricing &amp; tax</h2>
      <label>Price<input required type="number" min={0} step="0.01" value={form.unitPrice} onChange={set('unitPrice')} /></label>
      <label>Standard cost<input type="number" min={0} step="0.01" value={form.standardCost} onChange={set('standardCost')} /></label>
      <label>GST %<input type="number" min={0} max={100} step="0.01" value={form.taxRate} onChange={set('taxRate')} /></label>
      <label>HSN / SAC code<input maxLength={20} value={form.hsnCode} onChange={set('hsnCode')} /></label>
      <h2>Stock &amp; planning</h2>
      <label>{product ? 'Stock (total)' : 'Opening stock'}
        <input required type="number" min={0} step={1} value={form.stockQuantity} onChange={set('stockQuantity')} /></label>
      <label>Reorder level<input type="number" min={0} step={1} value={form.reorderLevel} onChange={set('reorderLevel')} /></label>
      <label>Lead time (days)<input type="number" min={0} step={1} value={form.leadTimeDays} onChange={set('leadTimeDays')} /></label>
      <label className="check-label"><input type="checkbox" checked={form.isActive}
        onChange={(e) => setForm({ ...form, isActive: e.target.checked })} /> Active</label>
      <label className="span-all">Description<textarea rows={2} maxLength={1000} value={form.description} onChange={set('description')} /></label>
      <p className="hint span-all">
        {product ? 'Changing the stock posts the difference to the default store as an adjustment. Inactive items can’t be put on new documents.'
          : 'Opening stock goes into the default store, valued at the price.'}
      </p>
      <div className="actions">
        <button disabled={busy}>{busy ? 'Saving…' : 'Save'}</button>
        <button type="button" className="link" onClick={() => onDone(false)}>Cancel</button>
        {error && <span role="alert" className="error">{error}</span>}
      </div>
    </form>
  );
}

export function OrdersPage() {
  const { items, error } = useList<Order>(api.orders);
  return (
    <>
      <h1>Orders</h1>
      {error && <p role="alert" className="error">{error}</p>}
      <div className="card table-wrap">
        <table aria-label="Orders">
          <thead><tr><th>Date</th><th>Status</th><th>Items</th><th>Total</th></tr></thead>
          <tbody>
            {items.map((o) => (
              <tr key={o.id}>
                <td>{new Date(o.orderedAt).toLocaleDateString()}</td><td>{o.status}</td>
                <td>{o.items.length}</td><td>{inr.format(o.totalAmount)}</td>
              </tr>
            ))}
          </tbody>
        </table>
        <p className="hint">Latest 25 orders.</p>
      </div>
    </>
  );
}

export function AssistantPage() {
  return (
    <>
      <h1>AI Assistant</h1>
      <ChatPanel />
    </>
  );
}
