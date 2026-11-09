import { useEffect, useMemo, useState } from 'react';
import { RIGHT_KEYS, type PageRights, type SetRightsItem } from '../api/client';

export interface MatrixRow {
  pageId: number;
  pageName: string;
  /** Section heading the row is grouped under, e.g. "Master › Users". */
  section: string;
  /** Rights currently in force for this row (override, or the inherited default). */
  rights: PageRights;
  /** True if this row is an override / customisation rather than the inherited default. */
  isCustom: boolean;
  /** What the row falls back to when the override is removed. */
  defaultRights: PageRights;
  /** Optional note, e.g. where the effective rights come from. */
  note?: string;
}

interface Draft { rights: PageRights; useDefault: boolean; dirty: boolean }

interface Props {
  rows: MatrixRow[];
  editable: boolean;
  customLabel: string;   // e.g. "Overridden"
  resetLabel: string;    // e.g. "Use role default"
  onSave: (items: SetRightsItem[]) => Promise<void>;
}

/**
 * Checkbox grid: pages x (Visible, Restricted, Read, Write, Update, Delete).
 * Editing a row turns it into an override; "reset" drops the override. Only changed rows are saved.
 */
export function RightsMatrix({ rows, editable, customLabel, resetLabel, onSave }: Props) {
  const initial = useMemo(
    () => new Map(rows.map((r) => [r.pageId, { rights: r.rights, useDefault: !r.isCustom, dirty: false } as Draft])),
    [rows],
  );
  const [draft, setDraft] = useState(initial);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);
  useEffect(() => { setDraft(initial); setSaved(false); }, [initial]);

  const dirtyCount = [...draft.values()].filter((d) => d.dirty).length;

  function toggle(row: MatrixRow, key: keyof PageRights) {
    setSaved(false);
    setDraft((prev) => {
      const next = new Map(prev);
      const cur = prev.get(row.pageId)!;
      next.set(row.pageId, { rights: { ...cur.rights, [key]: !cur.rights[key] }, useDefault: false, dirty: true });
      return next;
    });
  }

  function reset(row: MatrixRow) {
    setSaved(false);
    setDraft((prev) => new Map(prev).set(row.pageId, { rights: row.defaultRights, useDefault: true, dirty: true }));
  }

  async function save() {
    setBusy(true);
    setError(null);
    try {
      const items: SetRightsItem[] = [...draft.entries()]
        .filter(([, d]) => d.dirty)
        .map(([pageId, d]) => ({ pageId, useDefault: d.useDefault, rights: d.useDefault ? null : d.rights }));
      await onSave(items);
      setSaved(true);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Save failed');
    } finally {
      setBusy(false);
    }
  }

  const sections = [...new Set(rows.map((r) => r.section))];

  return (
    <div className="rights">
      <div className="table-wrap">
        <table aria-label="Page rights">
          <thead>
            <tr>
              <th>Page</th>
              {RIGHT_KEYS.map((k) => <th key={k.key} className="center">{k.label}</th>)}
              <th>Status</th>
            </tr>
          </thead>
          {sections.map((m) => (
            <tbody key={m}>
              <tr className="group-row"><td colSpan={RIGHT_KEYS.length + 2}>{m}</td></tr>
              {rows.filter((r) => r.section === m).map((row) => {
                const d = draft.get(row.pageId)!;
                return (
                  <tr key={row.pageId} className={d.rights.isRestricted ? 'restricted' : undefined}>
                    <td>{row.pageName}</td>
                    {RIGHT_KEYS.map((k) => (
                      <td key={k.key} className="center">
                        <input
                          type="checkbox"
                          aria-label={`${row.pageName} ${k.label}`}
                          checked={d.rights[k.key]}
                          disabled={!editable || busy}
                          onChange={() => toggle(row, k.key)}
                        />
                      </td>
                    ))}
                    <td>
                      {d.useDefault ? <span className="hint">Default{row.note ? ` (${row.note})` : ''}</span>
                        : <span className="badge">{customLabel}</span>}
                      {editable && !d.useDefault && (
                        <button className="link small" onClick={() => reset(row)} disabled={busy}>{resetLabel}</button>
                      )}
                      {d.dirty && <span className="hint"> • unsaved</span>}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          ))}
        </table>
      </div>
      {editable && (
        <div className="actions">
          <button onClick={save} disabled={busy || dirtyCount === 0}>
            {busy ? 'Saving…' : `Save changes${dirtyCount ? ` (${dirtyCount})` : ''}`}
          </button>
          {saved && <span className="ok" role="status">Saved</span>}
          {error && <span className="error" role="alert">{error}</span>}
        </div>
      )}
      <p className="hint">Restricted blocks the page completely for this user/role, whatever the other boxes say.</p>
    </div>
  );
}
