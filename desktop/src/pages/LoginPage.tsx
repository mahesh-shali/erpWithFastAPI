import { useEffect, useState, type FormEvent } from 'react';
import { Navigate, useLocation, useNavigate } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { Icon } from '../components/Icon';

/** Modules shown on the sign-in screen; mirrors the top level of the side menu. */
const MODULES: { name: string; icon: string }[] = [
  { name: 'Sales & Labour Work', icon: 'cart' },
  { name: 'Purchase & Subcontract', icon: 'truck' },
  { name: 'Planning', icon: 'calendar' },
  { name: 'Production', icon: 'factory' },
  { name: 'HRMS', icon: 'people' },
  { name: 'Accounts', icon: 'wallet' },
  { name: 'Inspection (QC)', icon: 'check' },
  { name: 'Maintenance', icon: 'wrench' },
  { name: 'Reports', icon: 'chart' },
];

export function LoginPage() {
  const { login, isAuthenticated, ready } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  if (!ready) return <p className="hint splash" role="status">Restoring your session…</p>;
  if (isAuthenticated) return <Navigate to="/" replace />;

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    setBusy(true);
    try {
      await login(email, password);
      const from = (location.state as { from?: { pathname: string } } | null)?.from?.pathname ?? '/';
      navigate(from, { replace: true });
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Login failed');
    } finally {
      setBusy(false);
    }
  }

  return (
    <main className="login">
      <section className="login-hero" aria-label="About the platform">
        <p className="login-eyebrow">ERP Platform</p>
        <h2 className="login-tagline">Manufacturing, inventory, HR, accounts and analytics in one place.</h2>
        <ul className="login-modules">
          {MODULES.map((m) => (
            <li key={m.name}><Icon name={m.icon} size={16} /> {m.name}</li>
          ))}
        </ul>
        <div className="login-badges">
          <span>Role-based access</span><span>Real-time sync</span><span>AI assistant</span>
        </div>
      </section>

      <form onSubmit={onSubmit} className="card login-card" aria-label="login form">
        <h1>Welcome back</h1>
        <p className="hint">Sign in to your ERP workspace</p>
        <label>
          Email
          <input type="email" value={email} onChange={(e) => setEmail(e.target.value)} required autoComplete="username" />
        </label>
        <label>
          Password
          <input type="password" value={password} onChange={(e) => setPassword(e.target.value)} required
                 autoComplete="current-password" />
        </label>
        {error && <p role="alert" className="error">{error}</p>}
        <button type="submit" disabled={busy}>{busy ? 'Signing in…' : 'Sign in'}</button>
        <ServerSettings />
      </form>
    </main>
  );
}

/** Which ERP server this desktop app talks to. Changing it signs out (a session belongs to one server). */
function ServerSettings() {
  const [serverUrl, setServerUrl] = useState('');
  const [environment, setEnvironment] = useState<'local' | 'production' | null>(null);
  const [locked, setLocked] = useState(false);
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState('');
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    window.erp.settings.get().then((s) => {
      setServerUrl(s.serverUrl);
      setEnvironment(s.environment);
      setLocked(s.serverUrlLocked);
    }).catch(() => undefined);
  }, []);

  async function save() {
    setError(null);
    try {
      const s = await window.erp.settings.setServerUrl(draft);
      setServerUrl(s.serverUrl);
      setEditing(false);
    } catch (e) {
      // Electron prefixes errors thrown in the main process; keep only our message.
      setError(e instanceof Error ? e.message.replace(/^Error invoking remote method '[^']+': (Error: )?/, '') : 'Invalid URL');
    }
  }

  if (!editing) {
    return (
      <p className="hint server-line">
        {environment && (
          <span className={`env-badge env-${environment}`}>{environment === 'local' ? 'Local' : 'Production'}</span>
        )}{' '}
        Server: <code>{serverUrl || '…'}</code>{' '}
        {!locked && (
          <button type="button" className="link" onClick={() => { setDraft(serverUrl); setEditing(true); }}>Change</button>
        )}
      </p>
    );
  }
  return (
    <div className="server-edit">
      <label>
        Server URL
        <input value={draft} onChange={(e) => setDraft(e.target.value)} placeholder="https://erp.example.com" />
      </label>
      {error && <p role="alert" className="error">{error}</p>}
      <div className="row">
        <button type="button" onClick={() => void save()}>Save server</button>
        <button type="button" className="link" onClick={() => setEditing(false)}>Cancel</button>
      </div>
    </div>
  );
}
