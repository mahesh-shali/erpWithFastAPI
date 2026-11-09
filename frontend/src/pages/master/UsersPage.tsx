import { useCallback, useEffect, useState, type FormEvent } from 'react';
import { Link } from 'react-router-dom';
import { api, type OrgUser, type RoleDto } from '../../api/client';
import { usePermissions } from '../../auth/PermissionsContext';

const fmt = (d: string | null) => (d ? new Date(d).toLocaleString() : 'Never');

/** Master > Users. Roles offered = roles below your own (SuperAdmin: Admin, User; Admin: User). */
export function UsersPage() {
  const { can } = usePermissions();
  const [users, setUsers] = useState<OrgUser[]>([]);
  const [roles, setRoles] = useState<RoleDto[]>([]);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      const [u, r] = await Promise.all([api.users(), api.assignableRoles()]);
      setUsers(u);
      setRoles(r);
      setError(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load users');
    }
  }, []);
  useEffect(() => { void load(); }, [load]);

  async function run(action: () => Promise<unknown>) {
    try {
      await action();
      await load();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Action failed');
    }
  }

  const canUpdate = can('master-users', 'update');
  const canDelete = can('master-users', 'delete');
  const canSeeRights = can('master-user-rights', 'read');

  return (
    <>
      <h1>Users</h1>
      {error && <p role="alert" className="error">{error}</p>}
      {can('master-users', 'write') && roles.length > 0 && <CreateUser roles={roles} onCreated={load} />}
      <div className="card">
        <div className="table-wrap">
          <table aria-label="Users">
            <thead>
              <tr><th>Name</th><th>Email</th><th>Role</th><th>Last login</th><th>Actions</th></tr>
            </thead>
            <tbody>
              {users.map((u) => (
                <tr key={u.id}>
                  <td>{u.fullName}</td>
                  <td>{u.email}</td>
                  <td>
                    {u.canManage && canUpdate && roles.length > 1 ? (
                      <select aria-label={`Role of ${u.fullName}`} value={u.role}
                              onChange={(e) => run(() => api.changeRole(u.id, e.target.value))}>
                        {roles.map((r) => <option key={r.id} value={r.name}>{r.name}</option>)}
                      </select>
                    ) : <span className={`role role-${u.role.toLowerCase()}`}>{u.role}</span>}
                  </td>
                  <td>{fmt(u.lastLoginAt)}</td>
                  <td className="row-actions">
                    {canSeeRights && <Link to={`/master/user-rights?user=${u.id}`}>Rights</Link>}
                    {u.canManage && canDelete && (
                      <button className="link danger"
                              onClick={() => confirm(`Delete ${u.fullName}?`) && run(() => api.deleteUser(u.id))}>
                        Delete
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>
    </>
  );
}

function CreateUser({ roles, onCreated }: { roles: RoleDto[]; onCreated: () => Promise<void> }) {
  const [form, setForm] = useState({ fullName: '', email: '', password: '', role: roles[roles.length - 1].name });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const set = (k: keyof typeof form) => (e: { target: { value: string } }) => setForm({ ...form, [k]: e.target.value });

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await api.createUser(form);
      setForm({ ...form, fullName: '', email: '', password: '' });
      await onCreated();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not create user');
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="card form-grid" onSubmit={submit} aria-label="Create user">
      <h2>Add user</h2>
      <label>Full name<input required value={form.fullName} onChange={set('fullName')} /></label>
      <label>Email<input required type="email" value={form.email} onChange={set('email')} /></label>
      <label>Password<input required type="password" minLength={8} value={form.password} onChange={set('password')} /></label>
      <label>Role
        <select value={form.role} onChange={set('role')}>
          {roles.map((r) => <option key={r.id} value={r.name}>{r.name}</option>)}
        </select>
      </label>
      <div className="actions">
        <button disabled={busy}>{busy ? 'Creating…' : 'Create user'}</button>
        {error && <span role="alert" className="error">{error}</span>}
      </div>
    </form>
  );
}
