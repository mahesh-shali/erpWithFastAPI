import { useEffect, useState } from 'react';
import { api, type Customer, type ItemLookup, type Store } from '../../api/client';

export const PO_ROUTE = '/sales/customer-po';
export const WO_ROUTE = '/production/work-orders';
export const INVOICE_ROUTE = '/sales/invoices';

/** Status badge for any document (Draft, Confirmed, Submitted, Completed, Cancelled). */
export function DocPill({ status, label }: { status: string; label?: string }) {
  return <span className={`status status-${status.toLowerCase()}`}>{label ?? status}</span>;
}

/** Pickers for the sales screens. Each list loads only when asked for. */
export function useSalesLookups({ customers = false, items = false, stores = false } = {}) {
  const [data, setData] = useState<{ customers: Customer[]; items: ItemLookup[]; stores: Store[] }>({ customers: [], items: [], stores: [] });
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    let alive = true;
    Promise.all([
      customers ? api.lookupCustomers() : Promise.resolve([]),
      items ? api.lookupItems() : Promise.resolve([]),
      stores ? api.lookupStores() : Promise.resolve([]),
    ]).then(([c, i, s]) => alive && setData({ customers: c, items: i, stores: s }))
      .catch((e) => alive && setError(e instanceof Error ? e.message : 'Could not load the lists'));
    return () => { alive = false; };
  }, [customers, items, stores]);
  return { ...data, lookupError: error };
}

export function Pager({ page, total, pageSize, onPage }: { page: number; total: number; pageSize: number; onPage: (p: number) => void }) {
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

/** Runs a document action with busy / error / notice state (save, confirm, submit, cancel…). */
export function useDocAction() {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  async function act<T>(label: string, work: () => Promise<T>, after?: (r: T) => void) {
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      const r = await work();
      after?.(r);
      setNotice(label);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Something went wrong');
    } finally {
      setBusy(false);
    }
  }
  return { busy, error, setError, notice, act };
}
