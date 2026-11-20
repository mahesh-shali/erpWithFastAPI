import { useCallback, useEffect, useState, type FormEvent } from 'react';
import { api, type Store, type Uom } from '../../api/client';
import { usePermissions } from '../../auth/PermissionsContext';
import { errorText } from './format';

/** Master › Inventory Setup › Units of Measure. Units used by products can only be deactivated. */
export function UomsPage() {
  const { can } = usePermissions();
  const [rows, setRows] = useState<Uom[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [editing, setEditing] = useState<Uom | 'new' | null>(null);

  const load = useCallback(async () => {
    try { setRows(await api.uoms()); setError(null); } catch (e) { setError(errorText(e)); }
  }, []);
  useEffect(() => { void load(); }, [load]);

  async function remove(u: Uom) {
    if (!confirm(`Delete unit ${u.code}?`)) return;
    try { await api.deleteUom(u.id); await load(); } catch (e) { setError(errorText(e)); }
  }

  return (
    <>
      <div className="page-head">
        <h1>Units of measure</h1>
        {can('uom', 'write') && editing === null && <button onClick={() => setEditing('new')}>Add unit</button>}
      </div>
      {error && <p role="alert" className="error">{error}</p>}
      {editing !== null && (
        <UomForm uom={editing === 'new' ? null : editing} onDone={async (saved) => { setEditing(null); if (saved) await load(); }} />
      )}
      <div className="card table-wrap">
        <table aria-label="Units of measure">
          <thead><tr><th>Code</th><th>Name</th><th>Status</th><th /></tr></thead>
          <tbody>
            {rows.map((u) => (
              <tr key={u.id}>
                <td><code>{u.code}</code></td><td>{u.name}</td>
                <td>{u.isActive ? 'Active' : <span className="badge">Inactive</span>}</td>
                <td className="row-actions">
                  {can('uom', 'update') && <button className="link" onClick={() => setEditing(u)}>Edit</button>}
                  {can('uom', 'delete') && <button className="link danger" onClick={() => remove(u)}>Delete</button>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        {rows.length === 0 && <p className="hint">No units yet. Common ones: NOS (numbers), KG, MTR, LTR, BOX.</p>}
      </div>
    </>
  );
}

function UomForm({ uom, onDone }: { uom: Uom | null; onDone: (saved: boolean) => Promise<void> }) {
  const [form, setForm] = useState({ code: uom?.code ?? '', name: uom?.name ?? '', isActive: uom?.isActive ?? true });
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    try { await api.saveUom(form, uom?.id); await onDone(true); } catch (err) { setError(errorText(err)); setBusy(false); }
  }
  return (
    <form className="card form-grid" onSubmit={submit} aria-label={uom ? 'Edit unit' : 'Add unit'}>
      <h2>{uom ? `Edit ${uom.code}` : 'Add unit'}</h2>
      <label>Code<input required maxLength={10} value={form.code} onChange={(e) => setForm({ ...form, code: e.target.value })} /></label>
      <label>Name<input required maxLength={50} value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} /></label>
      <label className="check-label"><input type="checkbox" checked={form.isActive} onChange={(e) => setForm({ ...form, isActive: e.target.checked })} /> Active</label>
      <div className="actions">
        <button disabled={busy}>{busy ? 'Saving…' : 'Save'}</button>
        <button type="button" className="link" onClick={() => onDone(false)}>Cancel</button>
        {error && <span role="alert" className="error">{error}</span>}
      </div>
    </form>
  );
}

/** Master › Inventory Setup › Stores. One store is the default (Main Store): sales orders ship from it. */
export function StoresPage() {
  const { can } = usePermissions();
  const [rows, setRows] = useState<Store[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [editing, setEditing] = useState<Store | 'new' | null>(null);

  const load = useCallback(async () => {
    try { setRows(await api.stores()); setError(null); } catch (e) { setError(errorText(e)); }
  }, []);
  useEffect(() => { void load(); }, [load]);

  async function remove(s: Store) {
    if (!confirm(`Delete store ${s.name}?`)) return;
    try { await api.deleteStore(s.id); await load(); } catch (e) { setError(errorText(e)); }
  }

  return (
    <>
      <div className="page-head">
        <h1>Stores</h1>
        {can('stores', 'write') && editing === null && <button onClick={() => setEditing('new')}>Add store</button>}
      </div>
      {error && <p role="alert" className="error">{error}</p>}
      {editing !== null && (
        <StoreForm store={editing === 'new' ? null : editing} onDone={async (saved) => { setEditing(null); if (saved) await load(); }} />
      )}
      <div className="card table-wrap">
        <table aria-label="Stores">
          <thead><tr><th>Code</th><th>Name</th><th>Location</th><th>Status</th><th /></tr></thead>
          <tbody>
            {rows.map((s) => (
              <tr key={s.id}>
                <td><code>{s.code}</code></td>
                <td>{s.name} {s.isDefault && <span className="tag">Default</span>}</td>
                <td>{s.location ?? '—'}</td>
                <td>{s.isActive ? 'Active' : <span className="badge">Inactive</span>}</td>
                <td className="row-actions">
                  {can('stores', 'update') && <button className="link" onClick={() => setEditing(s)}>Edit</button>}
                  {can('stores', 'delete') && !s.isDefault && <button className="link danger" onClick={() => remove(s)}>Delete</button>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        <p className="hint">Sales orders ship from the default store. Stores with stock history can be deactivated but not deleted.</p>
      </div>
    </>
  );
}

function StoreForm({ store, onDone }: { store: Store | null; onDone: (saved: boolean) => Promise<void> }) {
  const [form, setForm] = useState({
    code: store?.code ?? '', name: store?.name ?? '', location: store?.location ?? '',
    isActive: store?.isActive ?? true, isDefault: store?.isDefault ?? false,
  });
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    try {
      await api.saveStore({ ...form, location: form.location.trim() || null }, store?.id);
      await onDone(true);
    } catch (err) { setError(errorText(err)); setBusy(false); }
  }
  return (
    <form className="card form-grid" onSubmit={submit} aria-label={store ? 'Edit store' : 'Add store'}>
      <h2>{store ? `Edit ${store.name}` : 'Add store'}</h2>
      <label>Code<input required maxLength={20} value={form.code} onChange={(e) => setForm({ ...form, code: e.target.value })} /></label>
      <label>Name<input required maxLength={100} value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} /></label>
      <label>Location<input maxLength={200} value={form.location} onChange={(e) => setForm({ ...form, location: e.target.value })} /></label>
      <label className="check-label"><input type="checkbox" checked={form.isActive} disabled={store?.isDefault}
        onChange={(e) => setForm({ ...form, isActive: e.target.checked })} /> Active</label>
      <label className="check-label"><input type="checkbox" checked={form.isDefault} disabled={store?.isDefault}
        onChange={(e) => setForm({ ...form, isDefault: e.target.checked })} /> Default store</label>
      <div className="actions">
        <button disabled={busy}>{busy ? 'Saving…' : 'Save'}</button>
        <button type="button" className="link" onClick={() => onDone(false)}>Cancel</button>
        {error && <span role="alert" className="error">{error}</span>}
      </div>
    </form>
  );
}
