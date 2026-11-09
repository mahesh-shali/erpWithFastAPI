import { useEffect, useState } from 'react';
import { api, type PageDto } from '../../api/client';

/** Master > Pages: the screens rights can be granted on. Defined in code and seeded by migration. */
export function PagesPage() {
  const [pages, setPages] = useState<PageDto[]>([]);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { api.pages().then(setPages).catch((e: Error) => setError(e.message)); }, []);

  return (
    <>
      <h1>Pages</h1>
      {error && <p role="alert" className="error">{error}</p>}
      <div className="card">
        <p className="hint">Pages are defined in code (code-first) and added by database migrations.</p>
        <div className="table-wrap">
          <table aria-label="Pages">
            <thead><tr><th>Module</th><th>Group</th><th>Page</th><th>Key</th><th>Route</th><th>Status</th></tr></thead>
            <tbody>
              {pages.map((p) => (
                <tr key={p.id}>
                  <td>{p.menu.module ?? '—'}</td><td>{p.menu.group ?? '—'}</td><td>{p.name}</td>
                  <td><code>{p.key}</code></td><td>{p.route}</td>
                  <td>{!p.isActive ? 'Inactive' : p.isImplemented ? 'Ready' : <span className="badge">Coming soon</span>}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>
    </>
  );
}
