import { useCallback, useEffect, useMemo, useState } from 'react';
import { api, sectionOf, type RoleDto, type RoleRights, type SetRightsItem } from '../../api/client';
import { usePermissions } from '../../auth/PermissionsContext';
import { RightsMatrix, type MatrixRow } from '../../components/RightsMatrix';

/** Master > Role Rights: this organization's default rights per role (users inherit them unless overridden). */
export function RoleRightsPage() {
  const { can } = usePermissions();
  const [roles, setRoles] = useState<RoleDto[]>([]);
  const [roleId, setRoleId] = useState<number | null>(null);
  const [rights, setRights] = useState<RoleRights | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api.roles().then((r) => {
      setRoles(r);
      setRoleId(r.find((x) => x.name === 'User')?.id ?? r[0]?.id ?? null);
    }).catch((e: Error) => setError(e.message));
  }, []);

  const load = useCallback(async () => {
    if (roleId == null) return;
    try {
      setRights(await api.roleRights(roleId));
      setError(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load role rights');
    }
  }, [roleId]);
  useEffect(() => { void load(); }, [load]);

  const rows = useMemo<MatrixRow[]>(() => (rights?.pages ?? []).map((p) => ({
    pageId: p.pageId, pageName: p.pageName, section: sectionOf(p.menu),
    rights: p.rights, isCustom: p.isCustomized, defaultRights: p.systemDefault,
  })), [rights]);

  async function save(items: SetRightsItem[]) {
    if (roleId != null) setRights(await api.setRoleRights(roleId, items));
  }

  return (
    <>
      <h1>Role rights</h1>
      {error && <p role="alert" className="error">{error}</p>}
      <div className="card">
        <label>Role
          <select aria-label="Role" value={roleId ?? ''} onChange={(e) => setRoleId(Number(e.target.value))}>
            {roles.map((r) => <option key={r.id} value={r.id}>{r.name}</option>)}
          </select>
        </label>
        <p className="hint">
          Defaults for everyone with this role in your organization. Individual users can still be
          overridden under User Rights.
        </p>
      </div>
      {rights && (
        <div className="card">
          {!rights.canEdit && (
            <p className="hint">
              {rights.role === 'SuperAdmin' ? 'SuperAdmin always has every right.' :
                'Read-only: you can only change defaults of roles below your own.'}
            </p>
          )}
          <RightsMatrix rows={rows} editable={rights.canEdit && can('master-role-rights', 'update')}
                        customLabel="Customised" resetLabel="Use system default" onSave={save} />
        </div>
      )}
    </>
  );
}
