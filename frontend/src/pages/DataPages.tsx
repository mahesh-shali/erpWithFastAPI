import { useCallback, useEffect, useState } from 'react';
import { api, type Customer, type Order, type Product } from '../api/client';
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
  const remove = async (p: Product) => {
    if (!confirm(`Delete ${p.name}?`)) return;
    try { await api.deleteProduct(p.id); await load(); } catch (e) { setError((e as Error).message); }
  };
  return (
    <>
      <h1>Products</h1>
      {error && <p role="alert" className="error">{error}</p>}
      <div className="card table-wrap">
        <table aria-label="Products">
          <thead>
            <tr><th>SKU</th><th>Name</th><th>Category</th><th>Price</th><th>Stock</th>{can('products', 'delete') && <th />}</tr>
          </thead>
          <tbody>
            {items.map((p) => (
              <tr key={p.id}>
                <td>{p.sku}</td><td>{p.name}</td><td>{p.category ?? '—'}</td>
                <td>{inr.format(p.unitPrice)}</td><td>{p.stockQuantity}</td>
                {can('products', 'delete') && (
                  <td><button className="link danger" onClick={() => remove(p)}>Delete</button></td>
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
