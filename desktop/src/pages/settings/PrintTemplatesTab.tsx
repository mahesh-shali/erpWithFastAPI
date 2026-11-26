import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react';
import { api, type PrintTemplateSummary, type RenderedTemplate, type SalesInvoiceSummary } from '../../api/client';
import { TemplateView } from '../../components/TemplateView';

const TYPE = 'sales-invoice' as const;
const message = (e: unknown, fallback: string) => (e instanceof Error ? e.message : fallback);
const fmt = (iso: string) => new Date(iso).toLocaleString();
/** Wait this long after the last keystroke before re-rendering the preview on the server. */
export const PREVIEW_DELAY_MS = 600;
const ZOOMS = [50, 75, 100] as const;

interface Draft { id: string | null; name: string; body: string; version: number }

/** Variables a sales-invoice template can use (kept in step with PrintTemplateRenderer.InvoiceModel on the API). */
const VARIABLES: [string, string][] = [
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
];

/**
 * Live preview of the template being edited: re-rendered on the server a moment after typing stops (and when the
 * preview invoice changes). A template that doesn't render keeps the last good preview on screen and shows why.
 */
function useLivePreview(body: string | null, invoiceId: string) {
  const [preview, setPreview] = useState<RenderedTemplate | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const [updating, setUpdating] = useState(false);
  const latest = useRef(0);

  const refresh = useCallback(async (text: string) => {
    const seq = ++latest.current;
    setUpdating(true);
    try {
      const r = await api.previewPrintTemplate({ documentType: TYPE, body: text, invoiceId: invoiceId || null });
      if (seq !== latest.current) return;                    // a newer edit is already on its way
      setPreview(r);
      setProblem(null);
    } catch (e) {
      if (seq === latest.current) setProblem(message(e, 'The preview could not be made'));
    } finally {
      if (seq === latest.current) setUpdating(false);
    }
  }, [invoiceId]);

  useEffect(() => {
    if (body === null) { latest.current++; setPreview(null); setProblem(null); return; }
    const t = setTimeout(() => { void refresh(body); }, PREVIEW_DELAY_MS);
    return () => clearTimeout(t);
  }, [body, refresh]);

  return { preview, problem, updating, refresh: () => (body === null ? undefined : refresh(body)) };
}

/**
 * Settings › Print templates (SuperAdmin only): the organization's own invoice layout. HTML + CSS with Scriban
 * placeholders ({{ invoice.number }}, {{ for line in lines }}…{{ end }}), rendered and sanitized by the API.
 * The editor sits next to a live preview. Saving creates a new version; issued invoices keep the version they were
 * issued with.
 */
export function PrintTemplatesTab() {
  const [list, setList] = useState<PrintTemplateSummary[] | null>(null);
  const [draft, setDraft] = useState<Draft | null>(null);
  const [invoices, setInvoices] = useState<SalesInvoiceSummary[]>([]);
  const [previewWith, setPreviewWith] = useState('');
  const [zoom, setZoom] = useState<(typeof ZOOMS)[number]>(75);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const live = useLivePreview(draft?.body ?? null, previewWith);

  const load = useCallback(async () => {
    try { setList(await api.printTemplates(TYPE)); } catch (e) { setError(message(e, 'Could not load the templates')); }
  }, []);
  useEffect(() => { void load(); }, [load]);
  useEffect(() => { api.salesInvoices({ page: 1 }).then((r) => setInvoices(r.items)).catch(() => undefined); }, []);

  async function run(label: string | null, work: () => Promise<void>) {
    setBusy(true); setError(null); setNotice(null);
    try { await work(); if (label) setNotice(label); } catch (e) { setError(message(e, 'Something went wrong')); } finally { setBusy(false); }
  }

  const startNew = () => run(null, async () => {
    const s = await api.printTemplateStarter(TYPE);
    setDraft({ id: null, name: 'Our invoice', body: s.body, version: 0 });
  });
  const edit = (t: PrintTemplateSummary) => run(null, async () => {
    const full = await api.printTemplate(t.id);
    setDraft({ id: full.id, name: full.name, body: full.body, version: full.version });
  });

  async function save(e: FormEvent, makeDefault = false) {
    e.preventDefault();
    if (!draft) return;
    await run(draft.id ? 'Saved as a new version. Invoices already issued keep their version.' : 'Template saved.', async () => {
      const saved = draft.id
        ? await api.updatePrintTemplate(draft.id, { name: draft.name.trim(), body: draft.body })
        : await api.createPrintTemplate({ documentType: TYPE, name: draft.name.trim(), body: draft.body, makeDefault });
      setDraft({ id: saved.id, name: saved.name, body: saved.body, version: saved.version });
      await load();
    });
  }

  const current = list?.find((t) => t.isDefault);
  return (
    <section className="card settings-card wide" aria-label="Print templates">
      <h2>Print templates</h2>
      <p className="hint">
        Your own invoice layout: HTML and CSS with placeholders such as <code>{'{{ invoice.number }}'}</code>. Scripts, links and
        outside images are removed; add a logo as an embedded <code>data:image/png;base64,…</code> image. Every save is a new version,
        and an invoice keeps the version it was issued with.
      </p>
      <p>Invoices print with: <strong>{current ? `${current.name} (version ${current.version})` : 'the built-in layout'}</strong></p>
      {error && <p role="alert" className="error">{error}</p>}
      {notice && <p role="status" className="ok">{notice}</p>}

      <div className="table-wrap">
        <table aria-label="Templates">
          <thead><tr><th>Name</th><th className="num">Version</th><th>Saved</th><th /><th /></tr></thead>
          <tbody>
            {list?.map((t) => (
              <tr key={t.id} className={draft?.id === t.id ? 'selected' : undefined}>
                <td>{t.name}</td><td className="num">{t.version}</td><td>{fmt(t.createdAt)}</td>
                <td>{t.isDefault && <span className="badge on">Default</span>}</td>
                <td className="row-actions">
                  <button className="link" disabled={busy} onClick={() => edit(t)}>Edit &amp; preview</button>
                  {!t.isDefault && <button className="link" disabled={busy}
                    onClick={() => run(`${t.name} is now used for invoices.`, async () => { await api.setDefaultPrintTemplate(t.id); await load(); })}>Make default</button>}
                  <button className="link danger" disabled={busy} onClick={() => confirm(`Archive ${t.name}? Issued invoices that use it still print with it.`)
                    && run('Archived.', async () => { await api.archivePrintTemplate(t.id); if (draft?.id === t.id) setDraft(null); await load(); })}>Archive</button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        {list && list.length === 0 && <p className="hint">No templates yet: invoices use the built-in layout.</p>}
      </div>
      <div className="actions">
        <button disabled={busy} onClick={startNew}>New template</button>
        {current && <button className="link" disabled={busy}
          onClick={() => run('Invoices now print with the built-in layout.', async () => { await api.useBuiltInPrint(TYPE); await load(); })}>Use built-in layout</button>}
      </div>

      {draft && (
        <div className="template-workspace">
          <form className="template-editor" onSubmit={(e) => save(e)} aria-label="Print template">
            <h2>{draft.id ? `Edit ${draft.name} (version ${draft.version})` : 'New template'}</h2>
            <label>Name<input required maxLength={100} value={draft.name} onChange={(e) => setDraft({ ...draft, name: e.target.value })} /></label>
            <label>Template
              <textarea aria-label="Template" spellCheck={false} maxLength={200000} value={draft.body}
                onChange={(e) => setDraft({ ...draft, body: e.target.value })} />
            </label>
            <details className="template-vars">
              <summary>Variables you can use</summary>
              <dl>{VARIABLES.map(([k, v]) => <div key={k}><dt><code>{k}</code></dt><dd>{v}</dd></div>)}</dl>
              <p className="hint">Conditions: <code>{'{{ if invoice.is_inter_state }}…{{ else }}…{{ end }}'}</code>. In CSS, <code>body</code> means the page.</p>
            </details>
            <div className="actions">
              <button disabled={busy}>{draft.id ? 'Save new version' : 'Save'}</button>
              {!draft.id && <button type="button" disabled={busy} onClick={(e) => save(e as unknown as FormEvent, true)}>Save and use for invoices</button>}
              <button type="button" className="link" onClick={() => setDraft(null)}>Close</button>
            </div>
          </form>

          <section className="template-preview-pane" aria-label="Live preview">
            <div className="preview-bar">
              <h3>Live preview</h3>
              <label>With
                <select aria-label="Preview with" value={previewWith} onChange={(e) => setPreviewWith(e.target.value)}>
                  <option value="">Sample invoice</option>
                  {invoices.map((i) => <option key={i.id} value={i.id}>{i.number} · {i.customerName}</option>)}
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
