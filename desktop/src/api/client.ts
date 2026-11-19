import type { ErpBridge, HttpMethod } from '../../electron/shared/contract';

declare global {
  interface Window { erp: ErpBridge }
}

export class ApiError extends Error {
  constructor(public status: number, message: string) {
    super(message);
  }
}

export interface UserDto {
  id: string;
  email: string;
  fullName: string;
  role: 'SuperAdmin' | 'Admin' | 'User';
  organizationId: string;
}
/**
 * What the renderer learns about a session. Tokens never reach the renderer: the main process keeps the
 * access token in memory and the refresh token in OS-encrypted storage.
 */
export interface SessionInfo { user: UserDto; sessionExpiresAt: string | null; persistent: boolean }
export interface DashboardStats {
  customers: number; products: number; orders: number; revenue: number; lowStockProducts: number;
}
/** Analytics ranges: 30/90 days (daily points) or 365 = last 12 calendar months (monthly points). */
export type AnalyticsRange = 30 | 90 | 365;
export interface AnalyticsTotals { revenue: number; orders: number; averageOrderValue: number; newCustomers: number }
export interface AnalyticsPoint { date: string; revenue: number; orders: number }
export interface AnalyticsStatus { status: string; orders: number; amount: number }
/** count = units sold (products, categories) or orders (customers, cities). */
export interface AnalyticsRanked { name: string; revenue: number; count: number }
export interface AnalyticsReport {
  days: AnalyticsRange; bucket: 'day' | 'month'; from: string; to: string;
  current: AnalyticsTotals; previous: AnalyticsTotals;
  trend: AnalyticsPoint[]; statuses: AnalyticsStatus[];
  topProducts: AnalyticsRanked[]; topCustomers: AnalyticsRanked[]; cities: AnalyticsRanked[]; categories: AnalyticsRanked[];
}
export interface SessionDto {
  id: string; createdAt: string; lastSeenAt: string; expiresAt: string;
  ipAddress: string | null; userAgent: string | null; current: boolean;
}
export interface ChatTurn { role: 'user' | 'assistant'; content: string }
export interface ChatResponse {
  answer: string; sql: string[]; columns: string[]; rows: unknown[][]; cached: boolean; latency_ms: number;
}

let onUnauthorized: (() => void) | null = null;
export const setUnauthorizedHandler = (fn: (() => void) | null) => { onUnauthorized = fn; };

/** Turns a bridge response into data or an ApiError (server problem "title"/"detail" as the message). */
function unwrap<T>(res: { status: number; body: unknown }): T {
  if (res.status >= 200 && res.status < 300) return (res.status === 204 ? undefined : res.body) as T;
  const body = (res.body ?? {}) as { title?: string; detail?: unknown };
  const detail = typeof body.detail === 'string' ? body.detail : undefined;
  const fallback = res.status === 0 ? 'Cannot reach the ERP server' : 'Request failed';
  throw new ApiError(res.status, body.title ?? detail ?? fallback);
}

/**
 * Calls the API through the main process (window.erp). The main process attaches the access token,
 * refreshes it on a 401 and retries once; a 401 that still comes back here means the session is gone.
 */
export async function request<T>(path: string, init: { method?: string; body?: string } = {}): Promise<T> {
  const method = (init.method ?? 'GET').toUpperCase() as HttpMethod;
  const body = init.body === undefined ? undefined : JSON.parse(init.body);
  const res = await window.erp.api.request(method, path, body);
  if (res.status === 401) onUnauthorized?.();
  return unwrap<T>(res);
}

export const auth = {
  login: async (email: string, password: string) => unwrap<SessionInfo>(await window.erp.auth.login(email, password)),
  restore: () => window.erp.auth.restore() as Promise<SessionInfo | null>,
  logout: () => window.erp.auth.logout(),
  onSignedOut: (fn: () => void) => window.erp.auth.onSignedOut(fn),
};

// ---------- Master: pages & rights ----------
export type PageAction = 'read' | 'write' | 'update' | 'delete';

export interface PageRights {
  isVisible: boolean; isRestricted: boolean;
  canRead: boolean; canWrite: boolean; canUpdate: boolean; canDelete: boolean;
}
export const RIGHT_KEYS: { key: keyof PageRights; label: string }[] = [
  { key: 'isVisible', label: 'Visible' },
  { key: 'isRestricted', label: 'Restricted' },
  { key: 'canRead', label: 'Read' },
  { key: 'canWrite', label: 'Write' },
  { key: 'canUpdate', label: 'Update' },
  { key: 'canDelete', label: 'Delete' },
];

/** Position in the 3-level side menu (Module > Group > Page). module/group are null for top-level pages. */
export interface MenuPosition {
  moduleKey: string | null; module: string | null; moduleIcon: string | null; moduleSort: number;
  groupKey: string | null; group: string | null; groupSort: number; sortOrder: number;
}

export interface MyPagePermission extends PageRights {
  pageKey: string; name: string; route: string; menu: MenuPosition; isImplemented: boolean;
}
export interface MyPermissions { role: string; pages: MyPagePermission[] }

export interface PageDto {
  id: number; key: string; name: string; route: string; menu: MenuPosition; isActive: boolean; isImplemented: boolean;
}
export interface RoleDto { id: number; name: string; description: string | null; level: number }
export interface OrgUser {
  id: string; email: string; fullName: string; role: string; createdAt: string; lastLoginAt: string | null; canManage: boolean;
}
export interface UserRightsRow {
  pageId: number; pageKey: string; pageName: string; menu: MenuPosition;
  effective: PageRights; source: string; roleDefault: PageRights; isOverridden: boolean;
}
export interface UserRights { userId: string; fullName: string; email: string; role: string; canEdit: boolean; pages: UserRightsRow[] }
export interface RoleRightsRow {
  pageId: number; pageKey: string; pageName: string; menu: MenuPosition; rights: PageRights; isCustomized: boolean; systemDefault: PageRights;
}
export interface RoleRights { roleId: number; role: string; canEdit: boolean; pages: RoleRightsRow[] }
/** useDefault = drop the override and fall back to the role (or system) default. */
export interface SetRightsItem { pageId: number; useDefault: boolean; rights: PageRights | null }

// ---------- Business data ----------
export interface PagedResult<T> { items: T[]; page: number; pageSize: number; total: number }
export interface Customer { id: string; name: string; email: string | null; city: string | null; country: string | null }
export interface Product { id: string; sku: string; name: string; category: string | null; unitPrice: number; stockQuantity: number }
export interface Order { id: string; customerId: string; status: string; totalAmount: number; orderedAt: string; items: unknown[] }

const json = (body: unknown) => JSON.stringify(body);

/** "Master › Users" for menu pages, "General" for top-level pages. */
export const sectionOf = (m: MenuPosition) => (m.module ? `${m.module} › ${m.group}` : 'General');

export const api = {
  /** The caller's signed-in devices (desktop apps and browsers). */
  sessions: () => request<SessionDto[]>('/api/auth/sessions'),
  revokeSession: (id: string) => request<void>(`/api/auth/sessions/${id}`, { method: 'DELETE' }),
  logoutOthers: () => request<{ revoked: number }>('/api/auth/logout-others', { method: 'POST' }),
  stats: () => request<DashboardStats>('/api/dashboard/stats'),
  analytics: (days: AnalyticsRange) => request<AnalyticsReport>(`/api/analytics?days=${days}`),
  chat: (question: string, history: ChatTurn[]) =>
    request<ChatResponse>('/ai/chat', { method: 'POST', body: json({ question, history }) }),

  myPermissions: () => request<MyPermissions>('/api/me/permissions'),

  customers: (page = 1) => request<PagedResult<Customer>>(`/api/customers?page=${page}&pageSize=25`),
  deleteCustomer: (id: string) => request<void>(`/api/customers/${id}`, { method: 'DELETE' }),
  products: (page = 1) => request<PagedResult<Product>>(`/api/products?page=${page}&pageSize=25`),
  deleteProduct: (id: string) => request<void>(`/api/products/${id}`, { method: 'DELETE' }),
  orders: () => request<Order[]>('/api/orders?limit=25'),

  pages: () => request<PageDto[]>('/api/master/pages'),
  roles: () => request<RoleDto[]>('/api/roles'),
  assignableRoles: () => request<RoleDto[]>('/api/roles/assignable'),
  users: () => request<OrgUser[]>('/api/users'),
  createUser: (u: { email: string; fullName: string; password: string; role: string }) =>
    request<OrgUser>('/api/users', { method: 'POST', body: json(u) }),
  changeRole: (id: string, role: string) =>
    request<OrgUser>(`/api/users/${id}/role`, { method: 'PUT', body: json({ role }) }),
  deleteUser: (id: string) => request<void>(`/api/users/${id}`, { method: 'DELETE' }),
  userRights: (id: string) => request<UserRights>(`/api/master/users/${id}/rights`),
  setUserRights: (id: string, items: SetRightsItem[]) =>
    request<UserRights>(`/api/master/users/${id}/rights`, { method: 'PUT', body: json({ items }) }),
  roleRights: (roleId: number) => request<RoleRights>(`/api/master/roles/${roleId}/rights`),
  setRoleRights: (roleId: number, items: SetRightsItem[]) =>
    request<RoleRights>(`/api/master/roles/${roleId}/rights`, { method: 'PUT', body: json({ items }) }),
};
