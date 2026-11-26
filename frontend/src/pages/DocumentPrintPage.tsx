import { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { api, type DocumentPrint } from '../api/client';
import { PrintFormatPicker, useRequestedFormat } from '../components/PrintFormatPicker';
import { TemplateView } from '../components/TemplateView';

const KINDS = {
  'customer-pos': { load: api.customerPoPrint, back: '/sales/customer-po', label: 'Customer P.O.' },
  'work-orders': { load: api.workOrderPrint, back: '/production/work-orders', label: 'Work order' },
} as const;

/**
 * /sales/customer-po/:id/print and /production/work-orders/:id/print: the document rendered on the server with the
 * page's default print format (or the built-in layout), with a picker for the page's other formats. The browser's
 * print dialog makes the paper or PDF copy.
 */
export function DocumentPrintPage({ kind }: { kind: keyof typeof KINDS }) {
  const { id } = useParams();
  const [format, setFormat] = useRequestedFormat();
  const [data, setData] = useState<DocumentPrint | null>(null);
  const [error, setError] = useState<string | null>(null);
  const { load, back, label } = KINDS[kind];

  useEffect(() => {
    setError(null);
    load(id!, format).then(setData).catch((e) => setError(e instanceof Error ? e.message : 'Could not load the document'));
  }, [id, format, load]);
  useEffect(() => { if (data) document.title = data.title; }, [data]);

  if (error) return <p role="alert" className="error">{error}</p>;
  if (!data) return <p className="hint">Loading…</p>;
  const t = data.template;
  return (
    <div className="print-page">
      <div className="print-toolbar no-print">
        <Link to={`${back}/${data.documentId}`}>‹ Back to {data.number}</Link>
        <PrintFormatPicker formats={data.formats} selected={t.templateId} onChange={setFormat} />
        <span className="hint">{t.templateId ? `Layout: ${t.name} (version ${t.version})` : 'Built-in layout'}</span>
        <button onClick={() => window.print()}>Print</button>
      </div>
      {data.templateWarning && <p role="alert" className="error no-print print-warning">{data.templateWarning}</p>}
      <article className="invoice invoice-custom" aria-label={label}>
        <TemplateView html={t.html} css={t.css} label={`${data.number} (${t.name})`} />
      </article>
    </div>
  );
}
