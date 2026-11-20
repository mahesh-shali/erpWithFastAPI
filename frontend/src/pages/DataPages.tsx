import { useCallback, useEffect, useState, type FormEvent } from 'react';
import { api, type Customer, type Order, type Product, type Uom } from '../api/client';
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

const fetchCustomers = async () => (await api.customers()).items;
const fetchProducts = async () => (await api.products()).items;

/** Delete buttons appear only with the canDelete right on the page (the API checks it again). */
export function CustomersPage() {
  const { can } = usePermissions();
  const { items, error, setError, load } = useList<Customer>(fetchCustomers);
  const remove = async (c: Customer) => {
    if (!confirm(`Delete ${c.name}?`)) return;
    try { await api.deleteCustomer(c.id); await load(); } catch (e) { setError((e as Error).message); }
  };
  return (
    <>
      <h1>Customers</h1>
      {error && <p role="alert" className="error">{error}</p>}
      <div className="card table-wrap">
        <table aria-label="Customers">
          <thead><tr><th>Name</th><th>Email</th><th>City</th><th>Country</th>{can('customers', 'delete') && <th />}</tr></thead>
          <tbody>
            {items.map((c) => (
              <tr key={c.id}>
                <td>{c.name}</td><td>{c.email ?? '—'}</td><td>{c.city ?? '—'}</td><td>{c.country ?? '—'}</td>
                {can('customers', 'delete') && (
                  <td><button className="link danger" onClick={() => remove(c)}>Delete</button></td>
                )}
              </tr>
            ))}
          </tbody>
        </table>
        <p className="hint">Showing the first 25.</p>
      </div>
    </>
  );
}

export function ProductsPage() {
  const { can } = usePermissions();
  const { items, error, setError, load } = useList<Product>(fetchProducts);
  const [editing, setEditing] = useState<Product | 'new' | null>(null);
  const remove = async (p: Product) => {
    if (!confirm(`Delete ${p.name}?`)) return;
    try { await api.deleteProduct(p.id); await load(); } catch (e) { setError((e as Error).message); }
  };
  const actions = can('products', 'update') || can('products', 'delete');
  return (
    <>
      <div className="page-head">
        <h1>Products</h1>
        {can('products', 'write') && editing === null && <button onClick={() => setEditing('new')}>Add product</button>}
      </div>
      {error && <p role="alert" className="error">{error}</p>}
      {editing !== null && (
        <ProductForm product={editing === 'new' ? null : editing}
          onDone={async (saved) => { setEditing(null); if (saved) await load(); }} />
      )}
      <div className="card table-wrap">
        <table aria-label="Products">
          <thead>
            <tr><th>SKU</th><th>Name</th><th>Category</th><th>Unit</th><th>Price</th><th>Stock</th>{actions && <th />}</tr>
          </thead>
          <tbody>
            {items.map((p) => (
              <tr key={p.id}>
                <td>{p.sku}</td><td>{p.name}</td><td>{p.category ?? '—'}</td><td>{p.uom ?? '—'}</td>
                <td>{inr.format(p.unitPrice)}</td><td>{p.stockQuantity}</td>
                {actions && (
                  <td className="row-actions">
                    {can('products', 'update') && <button className="link" onClick={() => setEditing(p)}>Edit</button>}
                    {can('products', 'delete') && <button className="link danger" onClick={() => remove(p)}>Delete</button>}
                  </td>
                )}
              </tr>
            ))}
          </tbody>
        </table>
        <p className="hint">Showing the first 25. Stock is the total over all stores; see Inventory › Stock Position per store.</p>
      </div>
    </>
  );
}

/** Opening stock (new product) goes into the default store; a changed stock figure is posted there as an adjustment. */
function ProductForm({ product, onDone }: { product: Product | null; onDone: (saved: boolean) => Promise<void> }) {
  const [uoms, setUoms] = useState<Uom[]>([]);
  const [form, setForm] = useState({
    sku: product?.sku ?? '', name: product?.name ?? '', category: product?.category ?? '',
    unitPrice: String(product?.unitPrice ?? ''), stockQuantity: String(product?.stockQuantity ?? 0), uomId: product?.uomId ?? '',
  });
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  useEffect(() => { api.lookupUoms().then(setUoms).catch(() => undefined); }, []);
  const set = (k: keyof typeof form) => (e: { target: { value: string } }) => setForm({ ...form, [k]: e.target.value });

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    const body = {
      sku: form.sku.trim(), name: form.name.trim(), category: form.category.trim() || null,
      unitPrice: Number(form.unitPrice), stockQuantity: Number(form.stockQuantity), uomId: form.uomId || null,
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
    <form className="card form-grid" onSubmit={submit} aria-label={product ? 'Edit product' : 'Add product'}>
      <h2>{product ? `Edit ${product.name}` : 'Add product'}</h2>
      <label>SKU<input required maxLength={50} value={form.sku} onChange={set('sku')} /></label>
      <label>Name<input required maxLength={200} value={form.name} onChange={set('name')} /></label>
      <label>Category<input value={form.category} onChange={set('category')} /></label>
      <label>Unit
        <select aria-label="Unit" value={form.uomId} onChange={set('uomId')}>
          <option value="">—</option>{uoms.map((u) => <option key={u.id} value={u.id}>{u.code} · {u.name}</option>)}
        </select>
      </label>
      <label>Price<input required type="number" min={0} step="0.01" value={form.unitPrice} onChange={set('unitPrice')} /></label>
      <label>{product ? 'Stock (total)' : 'Opening stock'}
        <input required type="number" min={0} step={1} value={form.stockQuantity} onChange={set('stockQuantity')} /></label>
      <p className="hint span-all">
        {product ? 'Changing the stock posts the difference to the default store as an adjustment.'
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
