import { useEffect, useRef } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';

/**
 * Reopen the app where the user left it (same file in the web and desktop apps).
 *
 * The page (path + query) is remembered per user in localStorage. The first time the signed-in layout opens on
 * the start page — after a sign-in, a page load / app launch with a resumed session, or an update restart — it
 * goes back there. With no remembered page the user simply stays on the Dashboard.
 * Only a path is stored: no data, no tokens. Access is still checked by RequirePage and the API.
 */
const KEY = 'erp:last-page';
/** False until this sign-in / page load / launch has had its chance to go back to the last page. */
let restored = false;

type Saved = Record<string, string>; // userId -> "/path?query"

function read(): Saved {
  try {
    const v = JSON.parse(localStorage.getItem(KEY) ?? '{}') as unknown;
    return v && typeof v === 'object' ? (v as Saved) : {};
  } catch {
    return {};
  }
}

export function rememberPage(userId: string, page: string): void {
  try {
    localStorage.setItem(KEY, JSON.stringify({ ...read(), [userId]: page }));
  } catch {
    /* storage unavailable: just don't remember */
  }
}

export function lastPage(userId: string): string | null {
  const p = read()[userId];
  return typeof p === 'string' && p.startsWith('/') && !p.startsWith('//') && p !== '/login' ? p : null;
}

/** Call on every successful sign-in: the next start page opens where this user left off. */
export function restoreLastPageAfterSignIn(): void {
  restored = false;
}

/** For tests: lets the next mount restore again, as on a fresh launch. */
export function resetLastPageForTests(): void {
  restored = false;
}

/**
 * Use inside the signed-in layout. Returns true while the app is about to go back to the user's last page,
 * so the layout can keep a loading screen up instead of flashing the Dashboard.
 */
export function useLastPage(userId: string | undefined): boolean {
  const location = useLocation();
  const navigate = useNavigate();
  /**
   * Key of the location we are navigating away from while going back to the last page. Until the navigation lands
   * (the location key changes), the layout keeps the loading line up: a re-render in between (e.g. the rights
   * finishing loading) must not render the Dashboard for a moment and fetch its data.
   */
  const leaving = useRef<string | null>(null);
  const target = userId ? lastPage(userId) : null;
  const waiting = leaving.current !== null && leaving.current === location.key;
  const pending = waiting
    || (!!userId && !restored && location.pathname === '/' && !location.search && !!target && target !== '/');

  // Once per sign-in / launch: if we opened on the start page, go back to where this user was.
  // (Opening a deep link, e.g. a bookmark or the page that sent you to the login, wins over the remembered page.)
  useEffect(() => {
    if (!userId || restored) return;
    restored = true;
    if (pending && target) {
      leaving.current = location.key;
      navigate(target, { replace: true });
    }
    // Only on the first signed-in render.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [userId]);

  // Remember every page the user opens (skip the start page we are about to leave while restoring).
  useEffect(() => {
    if (!userId) return;
    if (leaving.current === location.key) return;
    leaving.current = null;
    rememberPage(userId, location.pathname + location.search);
  }, [userId, location.key, location.pathname, location.search]);

  return pending;
}
