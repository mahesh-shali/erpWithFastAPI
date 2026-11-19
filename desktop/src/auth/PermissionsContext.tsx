import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { api, type MyPagePermission, type PageAction } from '../api/client';
import { useAuth } from './AuthContext';

interface PermissionsState {
  loading: boolean;
  error: string | null;
  pages: MyPagePermission[];
  /** Pages to show in the menu: visible, readable and not restricted. */
  menu: MyPagePermission[];
  can: (pageKey: string, action?: PageAction) => boolean;
  reload: () => Promise<void>;
}

const PermissionsContext = createContext<PermissionsState | undefined>(undefined);

/** UI hint only: the API enforces the same rights on every request (and returns 403). */
export function allows(p: MyPagePermission | undefined, action: PageAction): boolean {
  if (!p || p.isRestricted) return false;
  switch (action) {
    case 'read': return p.canRead;
    case 'write': return p.canWrite;
    case 'update': return p.canUpdate;
    case 'delete': return p.canDelete;
  }
}

export function PermissionsProvider({ children }: { children: ReactNode }) {
  const { user } = useAuth();
  const [pages, setPages] = useState<MyPagePermission[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const reload = useCallback(async () => {
    if (!user) { setPages([]); setLoading(false); return; }
    setLoading(true);
    try {
      setPages((await api.myPermissions()).pages);
      setError(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not load permissions');
    } finally {
      setLoading(false);
    }
  }, [user]);

  useEffect(() => { void reload(); }, [reload]);

  const value = useMemo<PermissionsState>(() => {
    const byKey = new Map(pages.map((p) => [p.pageKey, p]));
    return {
      loading, error, pages, reload,
      menu: pages.filter((p) => p.isVisible && allows(p, 'read')),
      can: (key, action = 'read') => allows(byKey.get(key), action),
    };
  }, [pages, loading, error, reload]);

  return <PermissionsContext.Provider value={value}>{children}</PermissionsContext.Provider>;
}

export function usePermissions(): PermissionsState {
  const ctx = useContext(PermissionsContext);
  if (!ctx) throw new Error('usePermissions must be used within PermissionsProvider');
  return ctx;
}

/** Route guard: renders the page only if the user may read it (the API enforces the same rule). */
export function RequirePage({ page, children }: { page: string; children: ReactNode }) {
  const { loading, can } = usePermissions();
  if (loading) return <p className="hint">Loading…</p>;
  if (!can(page, 'read')) {
    return (
      <div className="card" role="alert">
        <h2>No access</h2>
        <p>You don't have permission to open this page. Ask your administrator for access.</p>
      </div>
    );
  }
  return <>{children}</>;
}
