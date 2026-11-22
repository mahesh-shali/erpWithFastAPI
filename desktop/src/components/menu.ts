import type { MyPagePermission } from '../api/client';

export interface MenuGroupNode { key: string; name: string; pages: MyPagePermission[] }
export interface MenuModuleNode { key: string; name: string; icon: string; groups: MenuGroupNode[] }
export interface MenuTree { top: MyPagePermission[]; modules: MenuModuleNode[] }

/**
 * Groups that are reached from the Settings screen (gear icon in the top bar) instead of the side menu.
 * Their pages keep their rights; they just don't appear in the menu.
 */
export const SETTINGS_GROUPS = new Set(['master-settings']);

/**
 * Rights-only entries: not screens, just switches in Role/User Rights (start chats with senior roles, edit users
 * in Settings). Never in the menu, even for SuperAdmin, who holds every right including "visible".
 */
export const RIGHTS_ONLY_PAGES = new Set(['messages-senior', 'settings-users']);

/** A page appears in the menu if it is visible, readable, not restricted, a real screen and not a Settings page. */
export const inMenu = (p: MyPagePermission) =>
  p.isVisible && p.canRead && !p.isRestricted
  && !RIGHTS_ONLY_PAGES.has(p.pageKey) && !SETTINGS_GROUPS.has(p.menu.groupKey ?? '');

/**
 * Builds the 3-level menu (Module > Group > Page) from the user's effective rights.
 * Groups/modules with no accessible page are dropped, so users never see empty branches.
 */
export function buildMenu(pages: MyPagePermission[]): MenuTree {
  const visible = pages.filter(inMenu).sort((a, b) =>
    a.menu.moduleSort - b.menu.moduleSort || a.menu.groupSort - b.menu.groupSort || a.menu.sortOrder - b.menu.sortOrder);

  const top = visible.filter((p) => !p.menu.moduleKey);
  const modules: MenuModuleNode[] = [];
  for (const p of visible.filter((x) => x.menu.moduleKey)) {
    let mod = modules.find((m) => m.key === p.menu.moduleKey);
    if (!mod) {
      mod = { key: p.menu.moduleKey!, name: p.menu.module!, icon: p.menu.moduleIcon ?? 'dot', groups: [] };
      modules.push(mod);
    }
    let group = mod.groups.find((g) => g.key === p.menu.groupKey);
    if (!group) {
      group = { key: p.menu.groupKey!, name: p.menu.group!, pages: [] };
      mod.groups.push(group);
    }
    group.pages.push(p);
  }
  return { top, modules };
}

/** Module and group keys on the path to the page at `pathname` (to auto-expand the active branch). */
export function activePath(tree: MenuTree, pathname: string): string[] {
  for (const m of tree.modules)
    for (const g of m.groups)
      if (g.pages.some((p) => p.route === pathname)) return [m.key, g.key];
  return [];
}
