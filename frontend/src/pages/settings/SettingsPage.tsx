import QRCode from 'qrcode';
import { useCallback, useEffect, useMemo, useState, type FormEvent, type ReactNode } from 'react';
import { NavLink, Navigate, useParams } from 'react-router-dom';
import { api, type Company, type OrgUser, type RoleDto } from '../../api/client';
import { settingsApi, type AccountInfo, type TwoFactorSetup } from '../../api/settings';
import { useAuth } from '../../auth/AuthContext';
import { usePermissions } from '../../auth/PermissionsContext';
import { PagesPage } from '../master/PagesPage';

const message = (e: unknown, fallback: string) => (e instanceof Error ? e.message : fallback);

/**
 * Settings: my profile, password and two-factor sign-in for everyone; "Users" for people who may edit
 * lower-role accounts (right "settings-users" + hierarchy: SuperAdmin > Admin > Manager > User); "Pages"
 * (the Pages master, moved here from the side menu) for people who may read "master-pages".
 * Tabs are URL segments: /settings, /settings/password, /settings/security, /settings/users, /settings/pages.
 */
export function SettingsPage() {
  const { tab = 'profile' } = useParams();
  const { can, loading } = usePermissions();
  const canEditUsers = can('settings-users', 'update');
  const canSeePages = can('master-pages', 'read');
  const [account, setAccount] = useState<AccountInfo | null>(null);
  const [error, setError] = useState<string | null>(null);

  // Company details (printed on invoices) are changed by administrators only; the API checks the role too.
  const canEditCompany = account?.role === 'SuperAdmin' || account?.role === 'Admin';

  const reload = useCallback(async () => {
    try {
      setAccount(await settingsApi.account());
      setError(null);
    } catch (e) {
      setError(message(e, 'Could not load your account'));
    }
  }, []);
  useEffect(() => { void reload(); }, [reload]);

  const tabs: [string, string][] = [
    ['profile', 'Profile'], ['password', 'Password'], ['security', 'Two-factor sign-in'],
    ...(canEditUsers ? [['users', 'Users'] as [string, string]] : []),
    ...(canSeePages ? [['pages', 'Pages'] as [string, string]] : []),
    ...(canEditCompany ? [['company', 'Company'] as [string, string]] : []),
  ];
  const rightsTab = tab === 'users' || tab === 'pages';
  // Wait for the rights (or, for Company, the account's role) before deciding that a tab is not allowed.
  if (!tabs.some(([k]) => k === tab) && !(rightsTab && loading) && !(tab === 'company' && !account && !error))
    return <Navigate to="/settings" replace />;

  return (
    <>
      <h1>Settings</h1>
      <nav className="tabs" aria-label="Settings sections">
        {tabs.map(([key, label]) => (
          <NavLink key={key} to={key === 'profile' ? '/settings' : `/settings/${key}`} end>{label}</NavLink>
        ))}
      </nav>
      {error && <p role="alert" className="error">{error}</p>}
      {tab === 'users' ? (canEditUsers ? <UsersTab /> : <p className="hint">Loading…</p>)
        : tab === 'pages' ? (canSeePages ? <PagesPage embedded /> : <p className="hint">Loading…</p>)
        : !account ? <p className="hint">Loading…</p> : (
        <>
          {tab === 'profile' && <ProfileTab account={account} onSaved={setAccount} />}
          {tab === 'password' && <PasswordTab />}
          {tab === 'security' && <TwoFactorTab account={account} onChanged={reload} />}
          {tab === 'company' && canEditCompany && <CompanyTab />}
        </>
      )}
    </>
  );
}

// ---------------- Profile ----------------

function ProfileTab({ account, onSaved }: { account: AccountInfo; onSaved: (a: AccountInfo) => void }) {
  const { updateUser } = useAuth();
  const [fullName, setFullName] = useState(account.fullName);
  const [email, setEmail] = useState(account.email);
  const [currentPassword, setCurrentPassword] = useState('');
  const emailChanged = email.trim().toLowerCase() !== account.email;

  return (
    <SaveForm label="Profile" submitText="Save profile" onSubmit={async () => {
      const saved = await settingsApi.updateProfile({ fullName, email, currentPassword: emailChanged ? currentPassword : null });
      onSaved(saved);
      updateUser({ fullName: saved.fullName, email: saved.email });
      setCurrentPassword('');
      return 'Profile saved.';
    }}>
      <label>Full name<input required maxLength={200} value={fullName} onChange={(e) => setFullName(e.target.value)} /></label>
      <label>Email<input required type="email" value={email} onChange={(e) => setEmail(e.target.value)} /></label>
      {emailChanged && (
        <label>Current password (to change your email)
          <input required type="password" autoComplete="current-password" value={currentPassword}
                 onChange={(e) => setCurrentPassword(e.target.value)} />
        </label>
      )}
      <label>Role<input value={account.role} disabled /></label>
    </SaveForm>
  );
}

// ---------------- Company ----------------

/** The organization's details on the printed invoice. GSTIN is upper-cased by the API. */
function CompanyTab() {
  const [company, setCompany] = useState<Company | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { api.company().then(setCompany).catch((e) => setError(message(e, 'Could not load the company'))); }, []);
  if (error) return <p role="alert" className="error">{error}</p>;
  if (!company) return <p className="hint">Loading…</p>;
  const set = (k: keyof Company) => (e: { target: { value: string } }) => setCompany({ ...company, [k]: e.target.value });
  const blank = (v: string | null) => (v && v.trim() ? v.trim() : null);

  return (
    <SaveForm label="Company" submitText="Save company" onSubmit={async () => {
      const saved = await api.saveCompany({ name: company.name.trim(), address: blank(company.address), state: blank(company.state),
        phone: blank(company.phone), email: blank(company.email), taxId: blank(company.taxId) });
      setCompany(saved);
      return 'Company details saved. They appear on every invoice print.';
    }}>
      <label>Company name<input required maxLength={200} value={company.name} onChange={set('name')} /></label>
      <label>GSTIN<input maxLength={20} value={company.taxId ?? ''} onChange={set('taxId')} /></label>
      <label>Address<textarea rows={3} maxLength={500} value={company.address ?? ''} onChange={set('address')} /></label>
      <label>State<input maxLength={100} value={company.state ?? ''} onChange={set('state')} /></label>
      <label>Phone<input maxLength={30} value={company.phone ?? ''} onChange={set('phone')} /></label>
      <label>Email<input type="email" maxLength={320} value={company.email ?? ''} onChange={set('email')} /></label>
      <p className="hint">The state decides the tax on each invoice: same state as the customer = CGST + SGST, another state = IGST.</p>
    </SaveForm>
  );
}

// ---------------- Password ----------------

function PasswordTab() {
  const [current, setCurrent] = useState('');
  const [next, setNext] = useState('');
  const [confirm, setConfirm] = useState('');

  return (
    <SaveForm label="Change password" submitText="Change password" onSubmit={async () => {
      if (next !== confirm) throw new Error('The new passwords do not match.');
      await settingsApi.changePassword(current, next);
      setCurrent(''); setNext(''); setConfirm('');
      return 'Password changed. Your other devices were signed out.';
    }}>
      <label>Current password
        <input required type="password" autoComplete="current-password" value={current} onChange={(e) => setCurrent(e.target.value)} />
      </label>
      <label>New password
        <input required type="password" minLength={8} autoComplete="new-password" value={next} onChange={(e) => setNext(e.target.value)} />
      </label>
      <label>Confirm new password
        <input required type="password" minLength={8} autoComplete="new-password" value={confirm} onChange={(e) => setConfirm(e.target.value)} />
      </label>
    </SaveForm>
  );
}

// ---------------- Two-factor sign-in ----------------

function TwoFactorTab({ account, onChanged }: { account: AccountInfo; onChanged: () => Promise<void> }) {
  const [setup, setSetup] = useState<TwoFactorSetup | null>(null);
  const [codes, setCodes] = useState<string[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function start() {
    setError(null);
    try {
      setSetup(await settingsApi.setupTwoFactor());
    } catch (e) {
      setError(message(e, 'Could not start the setup'));
    }
  }

  if (codes) {
    return <RecoveryCodesCard codes={codes} onDone={() => setCodes(null)} />;
  }

  if (!account.twoFactorEnabled) {
    return (
      <div className="card settings-card">
        <h2>Two-factor sign-in <span className="badge off">Off</span></h2>
        <p>Protect your account with a code from an authenticator app (Google Authenticator, Microsoft Authenticator,
          1Password…). Once it is on, signing in asks for the code after your password.</p>
        {error && <p role="alert" className="error">{error}</p>}
        {setup
          ? <EnableTwoFactor setup={setup} onEnabled={async (c) => { setSetup(null); setCodes(c); await onChanged(); }}
                             onCancel={() => setSetup(null)} />
          : <button onClick={start}>Set up two-factor sign-in</button>}
      </div>
    );
  }

  return (
    <div className="card settings-card">
      <h2>Two-factor sign-in <span className="badge on">On</span></h2>
      <p>Signing in needs a code from your authenticator app. Recovery codes left: <strong>{account.recoveryCodesLeft}</strong>
        {account.recoveryCodesLeft <= 3 && <span className="error"> (running low: create new ones)</span>}</p>
      <TrustedDevices count={account.trustedDevices ?? 0} onChanged={onChanged} />
      <div className="settings-split">
        <CodeAction title="New recovery codes" hint="The old recovery codes stop working." submitText="Create new codes"
                    onSubmit={async (code) => { setCodes((await settingsApi.regenerateRecoveryCodes(code)).recoveryCodes); await onChanged(); }} />
        <CodeAction title="Turn off" hint="Your account will be protected by the password only." submitText="Turn off two-factor"
                    danger withPassword
                    onSubmit={async (code, password) => { await settingsApi.disableTwoFactor(password!, code); await onChanged(); }} />
      </div>
    </div>
  );
}

/** Devices where "Remember this device for 30 days" was ticked at sign-in; they skip the code until then. */
function TrustedDevices({ count, onChanged }: { count: number; onChanged: () => Promise<void> }) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function forget() {
    if (!confirm('Ask for a code again on every remembered device, including this one?')) return;
    setBusy(true);
    setError(null);
    try {
      await settingsApi.forgetTrustedDevices();
      await onChanged();
    } catch (e) {
      setError(message(e, 'Could not forget the devices'));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="trusted-devices" aria-label="Remembered devices">
      <p>
        Remembered devices: <strong>{count}</strong>{' '}
        <span className="hint">(they skip the code for 30 days after you tick "Remember this device" when signing in)</span>
      </p>
      {count > 0 && (
        <button type="button" className="link danger" disabled={busy} onClick={forget}>Forget all remembered devices</button>
      )}
      {error && <p role="alert" className="error">{error}</p>}
    </div>
  );
}

function EnableTwoFactor({ setup, onEnabled, onCancel }: {
  setup: TwoFactorSetup; onEnabled: (codes: string[]) => Promise<void>; onCancel: () => void;
}) {
  const [qr, setQr] = useState<string | null>(null);
  const [code, setCode] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let live = true;
    // Generated here, in the app: the secret never goes to a third-party QR service.
    QRCode.toString(setup.otpAuthUri, { type: 'svg', margin: 1, width: 200 })
      .then((svg) => live && setQr(`data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}`))
      .catch(() => live && setQr(null));
    return () => { live = false; };
  }, [setup.otpAuthUri]);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await onEnabled((await settingsApi.enableTwoFactor(code)).recoveryCodes);
    } catch (err) {
      setError(message(err, 'That code did not work'));
      setCode('');
    } finally {
      setBusy(false);
    }
  }

  const grouped = useMemo(() => setup.secret.match(/.{1,4}/g)?.join(' ') ?? setup.secret, [setup.secret]);

  return (
    <form onSubmit={submit} className="twofa-setup" aria-label="Enable two-factor">
      <ol>
        <li>Scan this QR code with your authenticator app.
          <div className="qr">{qr ? <img src={qr} alt="QR code for your authenticator app" width={200} height={200} /> : <span className="hint">Preparing QR code…</span>}</div>
          <p className="hint">Can't scan? Enter this key in the app: <code className="secret" aria-label="Setup key">{grouped}</code></p>
        </li>
        <li>Enter the 6-digit code the app shows.
          <input aria-label="Code from the app" value={code} onChange={(e) => setCode(e.target.value)} required
                 inputMode="numeric" autoComplete="one-time-code" maxLength={8} placeholder="123456" />
        </li>
      </ol>
      {error && <p role="alert" className="error">{error}</p>}
      <div className="actions">
        <button disabled={busy || !code.trim()}>{busy ? 'Checking…' : 'Turn on'}</button>
        <button type="button" className="link" onClick={onCancel}>Cancel</button>
      </div>
    </form>
  );
}

function RecoveryCodesCard({ codes, onDone }: { codes: string[]; onDone: () => void }) {
  const [copied, setCopied] = useState(false);
  async function copy() {
    try {
      await navigator.clipboard.writeText(codes.join('\n'));
      setCopied(true);
    } catch { /* clipboard not available: the codes are on screen */ }
  }
  return (
    <div className="card settings-card" aria-label="Recovery codes">
      <h2>Save your recovery codes</h2>
      <p>Each code signs you in once if you lose your phone. Keep them somewhere safe: <strong>they are shown only now.</strong></p>
      <ul className="recovery-codes">{codes.map((c) => <li key={c}><code>{c}</code></li>)}</ul>
      <div className="actions">
        <button onClick={onDone}>I saved them</button>
        <button type="button" className="link" onClick={copy}>{copied ? 'Copied' : 'Copy codes'}</button>
      </div>
    </div>
  );
}

function CodeAction({ title, hint, submitText, danger, withPassword, onSubmit }: {
  title: string; hint: string; submitText: string; danger?: boolean; withPassword?: boolean;
  onSubmit: (code: string, password?: string) => Promise<void>;
}) {
  const [code, setCode] = useState('');
  const [password, setPassword] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await onSubmit(code, withPassword ? password : undefined);
    } catch (err) {
      setError(message(err, 'Action failed'));
      setCode('');
    } finally {
      setBusy(false);
    }
  }

  return (
    <form onSubmit={submit} aria-label={title} className="code-action">
      <h3>{title}</h3>
      <p className="hint">{hint}</p>
      {withPassword && (
        <label>Password<input type="password" required autoComplete="current-password" value={password}
                              onChange={(e) => setPassword(e.target.value)} /></label>
      )}
      <label>Authenticator or recovery code
        <input required value={code} onChange={(e) => setCode(e.target.value)} autoComplete="one-time-code" maxLength={32} />
      </label>
      {error && <p role="alert" className="error">{error}</p>}
      <button className={danger ? 'danger' : undefined} disabled={busy}>{busy ? 'Working…' : submitText}</button>
    </form>
  );
}

// ---------------- Users (lower roles) ----------------

function UsersTab() {
  const [users, setUsers] = useState<OrgUser[]>([]);
  const [roles, setRoles] = useState<RoleDto[]>([]);
  const [editing, setEditing] = useState<string | null>(null);
  const [search, setSearch] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      const [u, r] = await Promise.all([settingsApi.users(), settingsApi.assignableRoles()]);
      setUsers(u);
      setRoles(r);
      setError(null);
    } catch (e) {
      setError(message(e, 'Could not load users'));
    }
  }, []);
  useEffect(() => { void load(); }, [load]);

  const q = search.trim().toLowerCase();
  const visible = users.filter((u) => u.canManage && (!q || u.fullName.toLowerCase().includes(q) || u.email.includes(q)));

  async function resetTwoFactor(u: OrgUser) {
    if (!confirm(`Turn off two-factor sign-in for ${u.fullName}? They will be signed out and can sign in with their password.`)) return;
    try {
      await settingsApi.resetTwoFactor(u.id);
      setNotice(`Two-factor sign-in turned off for ${u.fullName}.`);
      await load();
    } catch (e) {
      setError(message(e, 'Could not reset two-factor sign-in'));
    }
  }

  return (
    <div className="card">
      <div className="settings-users-head">
        <p className="hint">You can edit people in roles below yours{roles.length ? ` (${roles.map((r) => r.name).join(', ')})` : ''}.</p>
        <input type="search" aria-label="Search users" placeholder="Search name or email" value={search}
               onChange={(e) => setSearch(e.target.value)} />
      </div>
      {error && <p role="alert" className="error">{error}</p>}
      {notice && <p className="ok" role="status">{notice}</p>}
      <div className="table-wrap">
        <table aria-label="Editable users">
          <thead><tr><th>Name</th><th>Email</th><th>Role</th><th>Two-factor</th><th>Actions</th></tr></thead>
          <tbody>
            {visible.length === 0 && <tr><td colSpan={5} className="hint">No users you can edit.</td></tr>}
            {visible.map((u) => editing === u.id ? (
              <tr key={u.id}><td colSpan={5}>
                <EditUserForm user={u} roles={roles} onCancel={() => setEditing(null)}
                              onSaved={async (msg) => { setEditing(null); setNotice(msg); await load(); }} />
              </td></tr>
            ) : (
              <tr key={u.id}>
                <td>{u.fullName}</td>
                <td>{u.email}</td>
                <td><span className={`role role-${u.role.toLowerCase()}`}>{u.role}</span></td>
                <td>{u.twoFactorEnabled ? <span className="badge on">On</span> : <span className="badge off">Off</span>}</td>
                <td className="row-actions">
                  <button className="link" onClick={() => { setNotice(null); setEditing(u.id); }}>Edit</button>
                  {u.twoFactorEnabled && <button className="link danger" onClick={() => resetTwoFactor(u)}>Reset 2FA</button>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function EditUserForm({ user, roles, onSaved, onCancel }: {
  user: OrgUser; roles: RoleDto[]; onSaved: (msg: string) => Promise<void>; onCancel: () => void;
}) {
  const [form, setForm] = useState({ fullName: user.fullName, email: user.email, role: user.role, newPassword: '' });
  const set = (k: keyof typeof form) => (e: { target: { value: string } }) => setForm({ ...form, [k]: e.target.value });
  // The current role is always selectable (keep it); the others are the roles below the caller.
  const options = roles.some((r) => r.name === user.role) ? roles.map((r) => r.name) : [user.role, ...roles.map((r) => r.name)];

  return (
    <SaveForm label={`Edit ${user.fullName}`} submitText="Save changes" onCancel={onCancel} onSubmit={async () => {
      await settingsApi.updateUser(user.id, {
        fullName: form.fullName, email: form.email,
        role: form.role === user.role ? null : form.role,
        newPassword: form.newPassword || null,
      });
      await onSaved(form.newPassword || form.role !== user.role
        ? `Saved ${form.fullName}. They were signed out so the change applies now.` : `Saved ${form.fullName}.`);
      return null;
    }}>
      <label>Full name<input required maxLength={200} value={form.fullName} onChange={set('fullName')} /></label>
      <label>Email<input required type="email" value={form.email} onChange={set('email')} /></label>
      <label>Role
        <select value={form.role} onChange={set('role')}>
          {options.map((r) => <option key={r} value={r}>{r}</option>)}
        </select>
      </label>
      <label>New password (optional)
        <input type="password" minLength={8} autoComplete="new-password" value={form.newPassword} onChange={set('newPassword')}
               placeholder="Leave empty to keep" />
      </label>
    </SaveForm>
  );
}

// ---------------- shared ----------------

/** Form card with busy state, error and success message. onSubmit returns the success text (or null). */
function SaveForm({ label, submitText, onSubmit, onCancel, children }: {
  label: string; submitText: string; onSubmit: () => Promise<string | null>; onCancel?: () => void; children: ReactNode;
}) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<string | null>(null);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    setDone(null);
    try {
      setDone(await onSubmit());
    } catch (err) {
      setError(message(err, 'Could not save'));
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="card form-grid" onSubmit={submit} aria-label={label}>
      <h2>{label}</h2>
      {children}
      <div className="actions">
        <button disabled={busy}>{busy ? 'Saving…' : submitText}</button>
        {onCancel && <button type="button" className="link" onClick={onCancel}>Cancel</button>}
        {error && <span role="alert" className="error">{error}</span>}
        {done && <span role="status" className="ok">{done}</span>}
      </div>
    </form>
  );
}
