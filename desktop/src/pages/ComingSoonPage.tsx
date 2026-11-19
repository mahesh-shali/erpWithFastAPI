import { Navigate, useLocation } from 'react-router-dom';
import { RequirePage, usePermissions } from '../auth/PermissionsContext';

/**
 * Catch-all inside the app shell: any route that belongs to a page in the Pages master but has no
 * screen yet shows "Coming soon" (still guarded by the page's read right). Unknown routes go home.
 */
export function ComingSoonRoute() {
  const { pages, loading } = usePermissions();
  const { pathname } = useLocation();
  if (loading) return <p className="hint">Loading…</p>;
  const page = pages.find((p) => p.route === pathname);
  if (!page) return <Navigate to="/" replace />;
  return (
    <RequirePage page={page.pageKey}>
      <h1>{page.name}</h1>
      <div className="card coming-soon">
        <p className="crumbs">{[page.menu.module, page.menu.group, page.name].filter(Boolean).join(' › ')}</p>
        <h2>Coming soon</h2>
        <p className="hint">
          This screen is part of the menu and the rights matrix already, so access can be granted now.
          The screen itself hasn't been built yet.
        </p>
      </div>
    </RequirePage>
  );
}
