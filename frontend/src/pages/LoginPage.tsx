import { useState, type FormEvent } from 'react';
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
  const { login, isAuthenticated } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  /** Step 2: the password was right and the account has two-factor sign-in on. */
  const [needsCode, setNeedsCode] = useState(false);
  const [code, setCode] = useState('');
  const [rememberDevice, setRememberDevice] = useState(false);

  if (isAuthenticated) return <Navigate to="/" replace />;

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    setBusy(true);
    try {
      if (await login(email, password, needsCode ? code : undefined, needsCode && rememberDevice) === 'two-factor') {
        setNeedsCode(true);
        return;
      }
      const from = (location.state as { from?: { pathname: string } } | null)?.from?.pathname ?? '/';
      navigate(from, { replace: true });
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Login failed');
      if (needsCode) setCode('');
    } finally {
      setBusy(false);
    }
  }

  function backToPassword() {
    setNeedsCode(false);
    setCode('');
    setPassword('');
    setError(null);
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

      {needsCode ? (
        <form onSubmit={onSubmit} className="card login-card" aria-label="two-factor form">
          <h1>Two-step verification</h1>
          <p className="hint">Enter the 6-digit code from your authenticator app for <strong>{email}</strong>.
            Lost your phone? Enter one of your recovery codes instead.</p>
          <label>
            Verification code
            <input value={code} onChange={(e) => setCode(e.target.value)} required autoFocus maxLength={32}
                   inputMode="numeric" autoComplete="one-time-code" placeholder="123456" />
          </label>
          <label className="check">
            <input type="checkbox" checked={rememberDevice} onChange={(e) => setRememberDevice(e.target.checked)} />
            Remember this device for 30 days
          </label>
          {error && <p role="alert" className="error">{error}</p>}
          <button type="submit" disabled={busy || !code.trim()}>{busy ? 'Verifying…' : 'Verify and sign in'}</button>
          <button type="button" className="link" onClick={backToPassword}>Back to sign in</button>
        </form>
      ) : (
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
        <p className="hint">Demo: admin@demo.com / Admin@123</p>
      </form>
      )}
    </main>
  );
}
