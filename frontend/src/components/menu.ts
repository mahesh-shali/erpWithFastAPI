import type { MyPagePermission } from '../api/client';

export interface MenuGroupNode { key: string; name: string; pages: MyPagePermission[] }
export interface MenuModuleNode { key: string; name: string; icon: string; groups: MenuGroupNode[] }
export interface MenuTree { top: MyPagePermission[]; modules: MenuModuleNode[] }

/** A page appears in the menu if it is visible, readable and not restricted. */
export const inMenu = (p: MyPagePermission) => p.isVisible && p.canRead && !p.isRestricted;

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
