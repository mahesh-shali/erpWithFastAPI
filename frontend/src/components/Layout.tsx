import { useEffect, useMemo, useState } from 'react';
import { NavLink, Outlet, useLocation } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { usePermissions } from '../auth/PermissionsContext';
import { Icon } from './Icon';
import { activePath, buildMenu, type MenuModuleNode } from './menu';

const TOP_ICONS: Record<string, string> = { dashboard: 'home', 'ai-chat': 'sparkle' };

/**
 * App shell. Desktop: fixed sidebar with a 3-level accordion (Module > Group > Page).
 * Mobile (< 900px): the same menu slides in as a drawer from the hamburger button.
 * The menu is built from the user's page rights (visible + read + not restricted).
 */
export function Layout() {
  const { user, logout } = useAuth();
  const { pages, loading, error } = usePermissions();
  const location = useLocation();
  const tree = useMemo(() => buildMenu(pages), [pages]);

  const [drawerOpen, setDrawerOpen] = useState(false);
  const [open, setOpen] = useState<Set<string>>(new Set());

  // Expand the branch of the current page; close the mobile drawer after navigating.
  useEffect(() => {
    const path = activePath(tree, location.pathname);
    if (path.length) setOpen((prev) => new Set([...prev, ...path]));
    setDrawerOpen(false);
  }, [tree, location.pathname]);

  // Mobile drawer: Escape closes it, and the page behind must not scroll.
  useEffect(() => {
    if (!drawerOpen) return;
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && setDrawerOpen(false);
    document.addEventListener('keydown', onKey);
    document.body.classList.add('no-scroll');
    return () => {
      document.removeEventListener('keydown', onKey);
      document.body.classList.remove('no-scroll');
    };
  }, [drawerOpen]);

  const toggle = (key: string) =>
    setOpen((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key); else next.add(key);
      return next;
    });

  return (
    <div className="layout">
      <header className="topbar">
        <button className="icon-btn hamburger" aria-label={drawerOpen ? 'Close menu' : 'Open menu'}
                aria-expanded={drawerOpen} aria-controls="side-nav" onClick={() => setDrawerOpen((o) => !o)}>
          <Icon name={drawerOpen ? 'close' : 'menu'} size={22} />
        </button>
        <strong className="brand">ERP Platform</strong>
        <span className="user-chip" title={user?.email}>
          <span className="user-name">{user?.fullName}</span>
          <span className={`role role-${user?.role.toLowerCase()}`}>{user?.role}</span>
        </span>
        <button onClick={logout} className="link">Sign out</button>
      </header>

      <div className="shell">
        {drawerOpen && <div className="backdrop" onClick={() => setDrawerOpen(false)} aria-hidden="true" />}
        <nav id="side-nav" aria-label="Main menu" className={`sidebar${drawerOpen ? ' open' : ''}`}>
          {loading && <p className="hint">Loading menu…</p>}
          {error && <p className="error">{error}</p>}

          {tree.top.map((p) => (
            <NavLink key={p.pageKey} to={p.route} end className="nav-top">
              <Icon name={TOP_ICONS[p.pageKey] ?? 'dot'} /> {p.name}
            </NavLink>
          ))}

          {tree.modules.map((m) => (
            <ModuleSection key={m.key} module={m} open={open} toggle={toggle} />
          ))}
        </nav>

        <main>
          <Outlet />
        </main>
      </div>
    </div>
  );
}

function ModuleSection({ module: m, open, toggle }: { module: MenuModuleNode; open: Set<string>; toggle: (k: string) => void }) {
  const isOpen = open.has(m.key);
  return (
    <div className="nav-module">
      <button className={`nav-l1${isOpen ? ' expanded' : ''}`} aria-expanded={isOpen} onClick={() => toggle(m.key)}>
        <Icon name={m.icon} />
        <span>{m.name}</span>
        <Icon name="chevron" size={16} className="chev" />
      </button>
      {isOpen && (
        <div className="nav-groups">
          {m.groups.map((g) => {
            const gOpen = open.has(g.key);
            return (
              <div key={g.key}>
                <button className={`nav-l2${gOpen ? ' expanded' : ''}`} aria-expanded={gOpen} onClick={() => toggle(g.key)}>
                  <span>{g.name}</span>
                  <Icon name="chevron" size={14} className="chev" />
                </button>
                {gOpen && (
                  <div className="nav-pages">
                    {g.pages.map((p) => (
                      <NavLink key={p.pageKey} to={p.route} end className="nav-l3">
                        {p.name}
                        {!p.isImplemented && <span className="soon">soon</span>}
                      </NavLink>
                    ))}
                  </div>
                )}
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}
