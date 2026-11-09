import { useCallback, useEffect, useMemo, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { api, sectionOf, type OrgUser, type SetRightsItem, type UserRights } from '../../api/client';
import { usePermissions } from '../../auth/PermissionsContext';
import { RightsMatrix, type MatrixRow } from '../../components/RightsMatrix';

const SOURCE_LABEL: Record<string, string> = {
  superadmin: 'SuperAdmin: all rights', user: 'user override', organization: 'role default (customised)',
  system: 'role default', none: 'no rights',
};

/** Master > User Rights: per-user overrides on top of the role defaults. */
export function UserRightsPage() {
  const { can } = usePermissions();
  const [params, setParams] = useSearchParams();
  const selected = params.get('user') ?? '';
  const [users, setUsers] = useState<OrgUser[]>([]);
  const [rights, setRights] = useState<UserRights | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api.users().then(setUsers).catch((e: Error) => setError(e.message));
  }, []);

  const load = useCallback(async () => {
    if (!selected) { setRights(null); return; }
    try {
      setRights(await api.userRights(selected));
      setError(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load rights');
    }
  }, [selected]);
  useEffect(() => { void load(); }, [load]);

  const rows = useMemo<MatrixRow[]>(() => (rights?.pages ?? []).map((p) => ({
    pageId: p.pageId, pageName: p.pageName, section: sectionOf(p.menu),
    rights: p.effective, isCustom: p.isOverridden, defaultRights: p.roleDefault,
    note: p.isOverridden ? undefined : SOURCE_LABEL[p.source] ?? p.source,
  })), [rights]);

  async function save(items: SetRightsItem[]) {
    setRights(await api.setUserRights(selected, items));
  }

  const editable = !!rights?.canEdit && can('master-user-rights', 'update');

  return (
    <>
      <h1>User rights</h1>
      {error && <p role="alert" className="error">{error}</p>}
      <div className="card">
        <label>User
          <select aria-label="User" value={selected} onChange={(e) => setParams(e.target.value ? { user: e.target.value } : {})}>
            <option value="">Select a user…</option>
            {users.map((u) => <option key={u.id} value={u.id}>{u.fullName} ({u.role})</option>)}
          </select>
        </label>
      </div>
      {rights && (
        <div className="card">
          <h2>{rights.fullName} <span className={`role role-${rights.role.toLowerCase()}`}>{rights.role}</span></h2>
          {!rights.canEdit && (
            <p className="hint">
              {rights.role === 'SuperAdmin' ? 'SuperAdmin always has every right.' :
                'Read-only: you can only change rights of users below your own role (and never your own).'}
            </p>
          )}
          <RightsMatrix rows={rows} editable={editable} customLabel="Overridden" resetLabel="Use role default" onSave={save} />
        </div>
      )}
    </>
  );
}
