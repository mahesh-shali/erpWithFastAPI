import { useCallback, useEffect, useState } from 'react';
import { api, type SessionDto } from '../../api/client';
import { useAuth } from '../../auth/AuthContext';

const fmt = (iso: string) => new Date(iso).toLocaleString();

/** Describes a session's client from its User-Agent ("ERP-Desktop/1.0.0 (win32) …" or a browser). */
export function deviceLabel(ua: string | null): string {
  if (!ua) return 'Unknown device';
  const desktop = /ERP-Desktop\/([\d.]+) \((\w+)\)/.exec(ua);
  if (desktop) {
    const os = { win32: 'Windows', darwin: 'macOS', linux: 'Linux' }[desktop[2]] ?? desktop[2];
    return `Desktop app ${desktop[1]} on ${os}`;
  }
  if (/k6\//.test(ua)) return 'Load test (k6)';
  const browser = /(Edg|Firefox|Chrome|Safari)\//.exec(ua)?.[1];
  return browser ? `${browser === 'Edg' ? 'Edge' : browser} browser` : ua.slice(0, 60);
}

/** Account > Devices: every signed-in session of the current user, with remote sign-out. */
export function DevicesPage() {
  const { persistent } = useAuth();
  const [sessions, setSessions] = useState<SessionDto[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const load = useCallback(() => {
    api.sessions().then(setSessions).catch((e: Error) => setError(e.message));
  }, []);
  useEffect(load, [load]);

  async function revoke(id: string) {
    setError(null);
    try {
      await api.revokeSession(id);
      load();
    } catch (e) {
      setError((e as Error).message);
    }
  }

  async function revokeOthers() {
    setError(null);
    try {
      const r = await api.logoutOthers();
      setNotice(`Signed out ${r.revoked} other session${r.revoked === 1 ? '' : 's'}.`);
      load();
    } catch (e) {
      setError((e as Error).message);
    }
  }

  const others = sessions.filter((s) => !s.current).length;
  return (
    <>
      <h1>Signed-in devices</h1>
      {error && <p role="alert" className="error">{error}</p>}
      {notice && <p role="status" className="hint">{notice}</p>}
      {!persistent && (
        <p className="hint">This computer has no secure key store, so you will be asked to sign in each time the app starts.</p>
      )}
      <div className="card">
        <div className="table-wrap">
          <table aria-label="Sessions">
            <thead><tr><th>Device</th><th>IP address</th><th>Signed in</th><th>Last active</th><th>Expires</th><th /></tr></thead>
            <tbody>
              {sessions.map((s) => (
                <tr key={s.id}>
                  <td>{deviceLabel(s.userAgent)} {s.current && <span className="badge">This device</span>}</td>
                  <td>{s.ipAddress ?? '—'}</td>
                  <td>{fmt(s.createdAt)}</td>
                  <td>{fmt(s.lastSeenAt)}</td>
                  <td>{fmt(s.expiresAt)}</td>
                  <td>{!s.current && <button className="link" onClick={() => void revoke(s.id)}>Sign out</button>}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        {others > 0 && <button onClick={() => void revokeOthers()}>Sign out all other devices</button>}
      </div>
    </>
  );
}
