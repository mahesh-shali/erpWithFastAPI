import { useEffect, useRef } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';

/**
 * Reopen the app where the user left it: the page (path + query) is remembered per user, and on the first
 * screen after a launch (session restored, or after an update restart) the app goes back there.
 * Only a path is stored — no data, no tokens. Access is still checked by RequirePage and the API.
 */
const KEY = 'erp:last-page';
let restoredThisLaunch = false;

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

/** For tests: lets the next mount restore again, as on a fresh launch. */
export function resetLastPageForTests(): void {
  restoredThisLaunch = false;
}

/**
 * Use inside the signed-in layout. Returns true while the app is about to go back to the user's last page,
 * so the layout can keep the loading screen up instead of flashing the start page.
 */
export function useLastPage(userId: string | undefined): boolean {
  const location = useLocation();
  const navigate = useNavigate();
  const restoring = useRef(false);
  const target = userId ? lastPage(userId) : null;
  const pending = !!userId && !restoredThisLaunch && location.pathname === '/' && !location.search
    && !!target && target !== '/';

  // Once per launch: if we opened on the start page, go back to where this user was.
  useEffect(() => {
    if (!userId || restoredThisLaunch) return;
    restoredThisLaunch = true;
    if (pending && target) {
      restoring.current = true;
      navigate(target, { replace: true });
    }
    // Only on the first signed-in render.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [userId]);

  // Remember every page the user opens (skip the start page we are about to leave while restoring).
  useEffect(() => {
    if (!userId) return;
    if (restoring.current && location.pathname === '/') return;
    restoring.current = false;
    rememberPage(userId, location.pathname + location.search);
  }, [userId, location.pathname, location.search]);

  return pending;
}
