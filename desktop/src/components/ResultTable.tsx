const MAX_VISIBLE = 50;

export function ResultTable({ columns, rows }: { columns: string[]; rows: unknown[][] }) {
  if (!columns.length || !rows.length) return null;
  const visible = rows.slice(0, MAX_VISIBLE);
  return (
    <div className="table-wrap">
      <table>
        <thead>
          <tr>{columns.map((c) => <th key={c}>{c}</th>)}</tr>
        </thead>
        <tbody>
          {visible.map((r, i) => (
            <tr key={i}>{r.map((v, j) => <td key={j}>{v === null ? '—' : String(v)}</td>)}</tr>
          ))}
        </tbody>
      </table>
      {rows.length > MAX_VISIBLE && <p className="hint">Showing {MAX_VISIBLE} of {rows.length} rows</p>}
    </div>
  );
}
