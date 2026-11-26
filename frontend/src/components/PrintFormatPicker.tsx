import { useSearchParams } from 'react-router-dom';
import type { PrintFormat } from '../api/client';

export const BUILT_IN = 'built-in';

/** The format asked for in the URL (?format=…), or null for the document's usual one. */
export function useRequestedFormat(): [string | null, (format: string) => void] {
  const [params, setParams] = useSearchParams();
  return [params.get('format'), (format) => setParams({ format }, { replace: true })];
}

const label = (f: PrintFormat) =>
  `${f.name}${f.id && f.version ? ` (version ${f.version})` : ''}${f.isDefault ? ' · default' : ''}${f.isIssuedWith ? ' · issued with' : ''}`;

/**
 * Print screens' format picker: a page can have several print formats (Settings › Print templates); the default is
 * used unless the user picks another here. The choice goes into the URL so a reload keeps it.
 */
export function PrintFormatPicker({ formats, selected, onChange }: {
  formats: PrintFormat[] | null | undefined; selected: string | null; onChange: (format: string) => void;
}) {
  if (!formats || formats.length < 2) return null;
  return (
    <label className="print-format">Format
      <select aria-label="Print format" value={selected ?? BUILT_IN} onChange={(e) => onChange(e.target.value)}>
        {formats.map((f) => <option key={f.id ?? BUILT_IN} value={f.id ?? BUILT_IN}>{label(f)}</option>)}
      </select>
    </label>
  );
}
