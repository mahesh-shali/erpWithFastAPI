import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent } from 'react';
import { api, type PrintPageKey, type PrintSetup, type PrintTemplateSummary, type RenderedTemplate } from '../../api/client';
import { TemplateView } from '../../components/TemplateView';

const message = (e: unknown, fallback: string) => (e instanceof Error ? e.message : fallback);
const fmt = (iso: string) => new Date(iso).toLocaleString();
/** Wait this long after the last keystroke before re-rendering the preview on the server. */
export const PREVIEW_DELAY_MS = 600;
const ZOOMS = [50, 75, 100] as const;

interface Draft { id: string | null; name: string; body: string; version: number }

/** Per printable page: what its documents are called, a new format's name, and its template variables. */
const PAGES: Record<PrintPageKey, { documents: string; sample: string; newName: string; variables: [string, string][] }> = {
  'sales-invoices': {
    documents: 'invoices', sample: 'Sample invoice', newName: 'Our invoice',
    // Kept in step with PrintTemplateRenderer.InvoiceModel on the API.
    variables: [
      ['company.name / address_html / state / phone / email / gstin', 'Your company (Settings › Company)'],
      ['customer.name / address_html / gstin / state / ship_to_html', 'Billing details copied onto the invoice'],
      ['invoice.number / date / due_date / status', 'is_draft and is_cancelled are true/false'],
      ['invoice.customer_po_ref / our_ref / place_of_supply / remarks', 'Customer’s P.O. no., our CPO number'],
      ['invoice.sub_total / discount_total / taxable_total / tax_total', 'Numbers: format with | fmt.money'],
      ['invoice.cgst_total / sgst_total / igst_total / round_off / grand_total', 'is_inter_state decides IGST vs CGST+SGST'],
      ['invoice.amount_in_words / line_count', '“Rupees … Only”'],
      ['for line in lines', 'line.no, sku, name, hsn, unit, quantity, rate, discount_percent, amount, tax_rate, tax_amount, cgst, sgst, igst, total'],
      ['for t in tax_summary', 't.rate, taxable, tax, cgst, sgst, igst'],
      ['value | fmt.money  ·  value | fmt.number', '₹12,34,567.89  ·  12,34,567'],
    ],
  },
  'customer-pos': {
    documents: 'customer P.O.s', sample: 'Sample P.O.', newName: 'Our P.O. format',
    // Kept in step with PrintTemplateRenderer.CustomerPoModel on the API.
    variables: [
      ['company.name / address_html / state / phone / email / gstin', 'Your company (Settings › Company)'],
      ['customer.name / address_html / gstin / state / phone / email', 'From the customer master'],
      ['po.number / customer_po_no / date / delivery_date / status', 'Our CPO number and the customer’s own P.O. no.; is_draft, is_cancelled'],
      ['po.sub_total / tax_total / grand_total / amount_in_words', 'Numbers: format with | fmt.money'],
      ['po.remarks / line_count / total_quantity', ''],
      ['for line in lines', 'line.no, sku, name, hsn, unit, quantity, rate, amount, tax_rate, tax_amount, total, produced_qty, invoiced_qty, to_invoice'],
      ['value | fmt.money  ·  value | fmt.number', '₹12,34,567.89  ·  12,34,567'],
    ],
  },
  'work-orders': {
    documents: 'work orders', sample: 'Sample work order', newName: 'Our job sheet',
    // Kept in step with PrintTemplateRenderer.WorkOrderModel on the API.
    variables: [
      ['company.name / address_html / state / phone / email / gstin', 'Your company (Settings › Company)'],
      ['work_order.number / date / status', 'status: Draft, Completed or Cancelled; is_draft, is_cancelled'],
      ['work_order.item_name / item_sku / quantity / unit', 'What is produced'],
      ['work_order.customer_po_number / customer_name', 'Empty when producing for stock'],
      ['work_order.store / material_store / remarks', 'Receive into · materials from · instructions'],
      ['work_order.material_cost / other_cost / total_cost / unit_cost', 'Numbers: format with | fmt.money'],
      ['for m in materials', 'm.no, sku, name, unit, quantity, rate, amount'],
      ['value | fmt.money  ·  value | fmt.number', '₹12,34,567.89  ·  12,34,567'],
    ],
  },
};
const isPrintable = (key: string): key is PrintPageKey => key in PAGES;

/** Recent documents of the page (signed-in organization only) to preview a template with. */
async function recentDocuments(page: PrintPageKey): Promise<{ id: string; label: string }[]> {
  if (page === 'sales-invoices') return (await api.salesInvoices({ page: 1 })).items.map((i) => ({ id: i.id, label: `${i.number} · ${i.customerName}` }));
  if (page === 'customer-pos') return (await api.customerPos({ page: 1 })).items.map((p) => ({ id: p.id, label: `${p.number} · ${p.customerName}` }));
  return (await api.workOrders({ page: 1 })).items.map((w) => ({ id: w.id, label: `${w.number} · ${w.productName}` }));
}

/**
 * Live preview of the template being edited: re-rendered on the server a moment after typing stops (and when the
 * preview document changes). A template that doesn't render keeps the last good preview on screen and shows why.
 */
function useLivePreview(body: string | null, page: PrintPageKey, documentId: string, organizationId: string | null) {
  const [preview, setPreview] = useState<RenderedTemplate | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const [updating, setUpdating] = useState(false);
  const latest = useRef(0);

  const refresh = useCallback(async (text: string) => {
    const seq = ++latest.current;
    setUpdating(true);
    try {
      const r = await api.previewPrintTemplate({ pageKey: page, body: text, documentId: documentId || null }, organizationId);
      if (seq !== latest.current) return;                    // a newer edit is already on its way
      setPreview(r);
      setProblem(null);
    } catch (e) {
      if (seq === latest.current) setProblem(message(e, 'The preview could not be made'));
    } finally {
      if (seq === latest.current) setUpdating(false);
    }
  }, [page, documentId, organizationId]);

  useEffect(() => {
    if (body === null) { latest.current++; setPreview(null); setProblem(null); return; }
    const t = setTimeout(() => { void refresh(body); }, PREVIEW_DELAY_MS);
    return () => clearTimeout(t);
  }, [body, refresh]);

  return { preview, problem, updating, refresh: () => (body === null ? undefined : refresh(body)) };
}

/**
 * Settings › Print templates (SuperAdmin only): print formats per Organization › Module › Submodule › Page, e.g.
 * Demo Corp › Sales & Labour Work › Orders › Customer P.O. A page can have several formats; one is its default and the
 * print screen can switch to the others. A platform admin can pick any organization; everyone else works on their own.
 * Templates are HTML + CSS with Scriban placeholders ({{ po.number }}, {{ for line in lines }}…{{ end }}), rendered and
 * sanitized by the API, edited next to a live preview. Saving creates a new version; issued invoices keep theirs.
 */
export function PrintTemplatesTab() {
  const [setup, setSetup] = useState<PrintSetup | null>(null);
  const [orgId, setOrgId] = useState<string | null>(null);
  const [moduleKey, setModuleKey] = useState('');
  const [groupKey, setGroupKey] = useState('');
  const [pageKey, setPageKey] = useState('');
  const [list, setList] = useState<PrintTemplateSummary[] | null>(null);
  const [draft, setDraft] = useState<Draft | null>(null);
  const [documents, setDocuments] = useState<{ id: string; label: string }[]>([]);
  const [previewWith, setPreviewWith] = useState('');
  const [zoom, setZoom] = useState<(typeof ZOOMS)[number]>(75);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  /** The signed-in organization (the first setup call is always for it). */
  const [homeOrgId, setHomeOrgId] = useState<string | null>(null);
  const ownOrg = !orgId || orgId === homeOrgId;
  /** organizationId sent to the API: null for the signed-in organization. */
  const target = setup && orgId && !ownOrg ? orgId : null;
  const page = isPrintable(pageKey) ? pageKey : null;
  const live = useLivePreview(draft?.body ?? null, page ?? 'sales-invoices', ownOrg ? previewWith : '', target);

  const module = setup?.modules.find((m) => m.key === moduleKey);
  const group = module?.groups.find((g) => g.key === groupKey);
  const pageInfo = group?.pages.find((p) => p.key === pageKey);
  const orgName = setup?.organizations.find((o) => o.id === (orgId ?? setup.currentOrganizationId))?.name ?? '…';

  // The tree (and format counts) of the chosen organization. First time: start at the first printable page.
  const loadSetup = useCallback(async (organizationId: string | null) => {
    try {
      const s = await api.printSetup(organizationId);
      setSetup(s);
      if (organizationId !== null) return;
      // First load (the signed-in organization): start at the first page that can print.
      setHomeOrgId(s.currentOrganizationId);
      setOrgId(s.currentOrganizationId);
      const first = s.modules.find((x) => x.groups.some((g) => g.pages.some((p) => p.printable)));
      const g = first?.groups.find((x) => x.pages.some((p) => p.printable));
      setModuleKey(first?.key ?? '');
      setGroupKey(g?.key ?? '');
      setPageKey(g?.pages.find((p) => p.printable)?.key ?? '');
    } catch (e) { setError(message(e, 'Could not load the print setup')); }
  }, []);
  useEffect(() => { void loadSetup(null); }, [loadSetup]);

  const load = useCallback(async () => {
    if (!page) { setList(null); return; }
    try { setList(await api.printTemplates(page, target)); } catch (e) { setError(message(e, 'Could not load the formats')); }
  }, [page, target]);
  useEffect(() => { void load(); }, [load]);
  useEffect(() => {
    setDocuments([]); setPreviewWith('');
    if (page && ownOrg) recentDocuments(page).then(setDocuments).catch(() => undefined);
  }, [page, ownOrg]);

  async function run(label: string | null, work: () => Promise<void>) {
    setBusy(true); setError(null); setNotice(null);
    try { await work(); if (label) setNotice(label); } catch (e) { setError(message(e, 'Something went wrong')); } finally { setBusy(false); }
  }
  /** After a change: the format list and the tree's counts. */
  const reload = async () => { await load(); await loadSetup(orgId); };

  function chooseOrg(id: string) {
    setOrgId(id); setDraft(null); setNotice(null); setError(null);
    void loadSetup(id);
  }
  function chooseModule(key: string) {
    const m = setup?.modules.find((x) => x.key === key);
    const g = m?.groups.find((x) => x.pages.some((p) => p.printable)) ?? m?.groups[0];
    setModuleKey(key); setGroupKey(g?.key ?? '');
    setPageKey(g?.pages.find((p) => p.printable)?.key ?? g?.pages[0]?.key ?? ''); setDraft(null);
  }
  function chooseGroup(key: string) {
    const g = module?.groups.find((x) => x.key === key);
    setGroupKey(key); setPageKey(g?.pages.find((p) => p.printable)?.key ?? g?.pages[0]?.key ?? ''); setDraft(null);
  }

  const startNew = () => run(null, async () => {
    if (!page) return;
    const s = await api.printTemplateStarter(page);
    setDraft({ id: null, name: PAGES[page].newName, body: s.body, version: 0 });
  });
  const edit = (t: PrintTemplateSummary) => run(null, async () => {
    const full = await api.printTemplate(t.id, target);
    setDraft({ id: full.id, name: full.name, body: full.body, version: full.version });
  });

  async function save(e: FormEvent, makeDefault = false) {
    e.preventDefault();
    if (!draft || !page) return;
    await run(draft.id ? 'Saved as a new version. Invoices already issued keep their version.' : 'Format saved.', async () => {
      const saved = draft.id
        ? await api.updatePrintTemplate(draft.id, { name: draft.name.trim(), body: draft.body }, target)
        : await api.createPrintTemplate({ pageKey: page, name: draft.name.trim(), body: draft.body, makeDefault }, target);
      setDraft({ id: saved.id, name: saved.name, body: saved.body, version: saved.version });
      await reload();
    });
  }

  const pageMeta = page ? PAGES[page] : null;
  const current = list?.find((t) => t.isDefault);
  const trail = useMemo(() => [orgName, module?.name, group?.name, pageInfo?.name, 'Print'].filter(Boolean).join(' › '),
    [orgName, module, group, pageInfo]);

  return (
    <section className="card settings-card wide" aria-label="Print templates">
      <h2>Print templates</h2>
      <p className="hint">
        Print formats per page: choose the organization, module, submodule and page, then add as many formats as you need. One is the
        page’s default; the print screen can switch to any other. Formats are HTML and CSS with placeholders such
        as <code>{'{{ po.number }}'}</code>. Scripts, links and outside images are removed; add a logo as an
        embedded <code>data:image/png;base64,…</code> image. Every save is a new version, and an invoice keeps the version it was issued with.
      </p>
      {error && <p role="alert" className="error">{error}</p>}
      {notice && <p role="status" className="ok">{notice}</p>}

      <div className="print-scope" role="group" aria-label="Print format location">
        <label>Organization
          {setup?.isPlatformAdmin
            ? <select aria-label="Organization" value={orgId ?? ''} onChange={(e) => chooseOrg(e.target.value)}>
                {setup.organizations.map((o) => <option key={o.id} value={o.id}>{o.name}</option>)}
              </select>
            : <input aria-label="Organization" readOnly value={orgName} />}
        </label>
        <label>Module
          <select aria-label="Module" value={moduleKey} onChange={(e) => chooseModule(e.target.value)}>
            {setup?.modules.map((m) => <option key={m.key} value={m.key}>{m.name}</option>)}
          </select>
        </label>
        <label>Submodule
          <select aria-label="Submodule" value={groupKey} onChange={(e) => chooseGroup(e.target.value)}>
            {module?.groups.map((g) => <option key={g.key} value={g.key}>{g.name}</option>)}
          </select>
        </label>
        <label>Page
          <select aria-label="Page" value={pageKey} onChange={(e) => { setPageKey(e.target.value); setDraft(null); }}>
            {group?.pages.map((p) => (
              <option key={p.key} value={p.key} disabled={!p.printable}>
                {p.name}{p.printable ? (p.formatCount ? ` (${p.formatCount})` : '') : ' — no print yet'}
              </option>
            ))}
          </select>
        </label>
      </div>
      {setup && <p className="crumbs" aria-label="Selected page">{trail}</p>}
      {setup?.isPlatformAdmin && !ownOrg && (
        <p className="hint">You are editing another organization’s formats as platform admin. Previews use sample data with its company details.</p>
      )}

      {pageInfo && !page && <p className="hint">{pageInfo.name} has no print screen yet, so it has no formats.</p>}
      {page && pageMeta && (
        <>
          <p>{pageInfo?.name} prints with: <strong>{current ? `${current.name} (version ${current.version})` : 'the built-in layout'}</strong></p>
          <div className="table-wrap">
            <table aria-label="Formats">
              <thead><tr><th>Name</th><th className="num">Version</th><th>Saved</th><th /><th /></tr></thead>
              <tbody>
                {list?.map((t) => (
                  <tr key={t.id} className={draft?.id === t.id ? 'selected' : undefined}>
                    <td>{t.name}</td><td className="num">{t.version}</td><td>{fmt(t.createdAt)}</td>
                    <td>{t.isDefault && <span className="badge on">Default</span>}</td>
                    <td className="row-actions">
                      <button className="link" disabled={busy} onClick={() => edit(t)}>Edit &amp; preview</button>
                      {!t.isDefault && <button className="link" disabled={busy}
                        onClick={() => run(`${t.name} is now the default for ${pageMeta.documents}.`, async () => {
                          await api.setDefaultPrintTemplate(t.id, target); await reload();
                        })}>Make default</button>}
                      <button className="link danger" disabled={busy} onClick={() => confirm(`Archive ${t.name}? Issued invoices that use it still print with it.`)
                        && run('Archived.', async () => { await api.archivePrintTemplate(t.id, target); if (draft?.id === t.id) setDraft(null); await reload(); })}>Archive</button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
            {list && list.length === 0 && <p className="hint">No formats yet: {pageMeta.documents} use the built-in layout.</p>}
          </div>
          <div className="actions">
            <button disabled={busy} onClick={startNew}>New format</button>
            {current && <button className="link" disabled={busy}
              onClick={() => run(`${pageInfo?.name} now prints with the built-in layout.`, async () => {
                await api.useBuiltInPrint(page, target); await reload();
              })}>Use built-in layout</button>}
          </div>
        </>
      )}

      {draft && page && pageMeta && (
        <div className="template-workspace">
          <form className="template-editor" onSubmit={(e) => save(e)} aria-label="Print template">
            <h2>{draft.id ? `Edit ${draft.name} (version ${draft.version})` : `New format for ${pageInfo?.name ?? page}`}</h2>
            <label>Name<input required maxLength={100} value={draft.name} onChange={(e) => setDraft({ ...draft, name: e.target.value })} /></label>
            <label>Template
              <textarea aria-label="Template" spellCheck={false} maxLength={200000} value={draft.body}
                onChange={(e) => setDraft({ ...draft, body: e.target.value })} />
            </label>
            <details className="template-vars">
              <summary>Variables you can use</summary>
              <dl>{pageMeta.variables.map(([k, v]) => <div key={k}><dt><code>{k}</code></dt><dd>{v}</dd></div>)}</dl>
              <p className="hint">Conditions: <code>{'{{ if … }}…{{ else }}…{{ end }}'}</code>. In CSS, <code>body</code> means the page.</p>
            </details>
            <div className="actions">
              <button disabled={busy}>{draft.id ? 'Save new version' : 'Save'}</button>
              {!draft.id && <button type="button" disabled={busy} onClick={(e) => save(e as unknown as FormEvent, true)}>Save as default</button>}
              <button type="button" className="link" onClick={() => setDraft(null)}>Close</button>
            </div>
          </form>

          <section className="template-preview-pane" aria-label="Live preview">
            <div className="preview-bar">
              <h3>Live preview</h3>
              <label>With
                <select aria-label="Preview with" value={previewWith} disabled={!ownOrg} onChange={(e) => setPreviewWith(e.target.value)}>
                  <option value="">{pageMeta.sample}</option>
                  {documents.map((d) => <option key={d.id} value={d.id}>{d.label}</option>)}
                </select>
              </label>
              <label>Zoom
                <select aria-label="Zoom" value={zoom} onChange={(e) => setZoom(Number(e.target.value) as (typeof ZOOMS)[number])}>
                  {ZOOMS.map((z) => <option key={z} value={z}>{z}%</option>)}
                </select>
              </label>
              <button type="button" className="link" onClick={() => void live.refresh()}>Refresh</button>
              <span className="hint" role="status" aria-live="polite">{live.updating ? 'Updating…' : live.preview ? 'Up to date' : ''}</span>
            </div>
            {live.problem && (
              <p role="alert" className="error preview-problem">
                {live.problem}{live.preview && ' The last working preview is shown below.'}
              </p>
            )}
            <div className="paper-scroll">
              <div className={`paper zoom-${zoom}`}>
                {live.preview
                  ? <TemplateView html={live.preview.html} css={live.preview.css} label="Template preview" />
                  : <p className="hint">{live.problem ? 'Fix the template to see the preview.' : 'Preparing the preview…'}</p>}
              </div>
            </div>
          </section>
        </div>
      )}
    </section>
  );
}
