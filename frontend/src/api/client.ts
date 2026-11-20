export const TOKEN_KEY = 'erp.token';
export const USER_KEY = 'erp.user';

export class ApiError extends Error {
  constructor(public status: number, message: string) {
    super(message);
  }
}

export interface UserDto {
  id: string;
  email: string;
  fullName: string;
  role: 'SuperAdmin' | 'Admin' | 'Manager' | 'User';
  organizationId: string;
}
/** The refresh token never appears here: the server keeps it in an HttpOnly cookie. */
export interface AuthResponse {
  accessToken: string; expiresAt: string; user: UserDto; sessionId?: string; sessionExpiresAt?: string;
}
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
export interface ChatTurn { role: 'user' | 'assistant'; content: string }
export interface ChatResponse {
  answer: string; sql: string[]; columns: string[]; rows: unknown[][]; cached: boolean; latency_ms: number;
}

let onUnauthorized: (() => void) | null = null;
export const setUnauthorizedHandler = (fn: (() => void) | null) => { onUnauthorized = fn; };

export function storeSession(res: AuthResponse) {
  localStorage.setItem(TOKEN_KEY, res.accessToken);
  localStorage.setItem(USER_KEY, JSON.stringify(res.user));
}

// ---------- Silent session refresh ----------
// Access tokens are short-lived. On a 401 we ask /api/auth/refresh for a new one (the HttpOnly
// refresh cookie is sent automatically) and retry the request once. Concurrent 401s share one refresh.
let refreshing: Promise<boolean> | null = null;

export function refreshSession(): Promise<boolean> {
  refreshing ??= (async () => {
    try {
      const res = await fetch('/api/auth/refresh', { method: 'POST', credentials: 'same-origin' });
      if (!res.ok) return false;
      storeSession(await res.json());
      return true;
    } catch {
      return false;
    } finally {
      refreshing = null;
    }
  })();
  return refreshing;
}

const isAuthEndpoint = (path: string) => path.startsWith('/api/auth/');

export async function request<T>(path: string, init: RequestInit = {}, retried = false): Promise<T> {
  const token = localStorage.getItem(TOKEN_KEY);
  const headers = new Headers(init.headers);
  headers.set('Content-Type', 'application/json');
  if (token) headers.set('Authorization', `Bearer ${token}`);

  const res = await fetch(path, { ...init, headers, credentials: 'same-origin' });
  if (res.status === 401 && !isAuthEndpoint(path)) {
    if (!retried) {
      // Another tab may already have refreshed (the token in storage changed): just retry with it.
      const tokenChanged = localStorage.getItem(TOKEN_KEY) !== token;
      if (tokenChanged || (token && await refreshSession())) return request<T>(path, init, true);
    }
    onUnauthorized?.();
  }
  if (!res.ok) {
    let msg = res.statusText || 'Request failed';
    try {
      const body = await res.json();
      msg = body.title ?? body.detail ?? msg;
    } catch { /* non-JSON error body */ }
    throw new ApiError(res.status, msg);
  }
  return res.status === 204 ? (undefined as T) : res.json();
}

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
export interface Product {
  id: string; sku: string; name: string; category: string | null; unitPrice: number; stockQuantity: number;
  uomId?: string | null; uom?: string | null;
}
export interface UpsertProduct { sku: string; name: string; category: string | null; unitPrice: number; stockQuantity: number; uomId: string | null }

// ---------- Inventory ----------
export interface Uom { id: string; code: string; name: string; isActive: boolean }
export interface Store { id: string; code: string; name: string; location: string | null; isDefault: boolean; isActive: boolean }
export interface ItemLookup { id: string; sku: string; name: string; uom: string | null; stockQuantity: number; unitPrice: number }
export type StockEntryType = 'Receipt' | 'Issue' | 'Transfer';
export type DocStatus = 'Draft' | 'Submitted' | 'Cancelled';
export interface StockEntryLine { productId: string; sku: string; productName: string; uom: string | null; quantity: number; rate: number; amount: number }
export interface StockEntry {
  id: string; number: string; type: StockEntryType; status: DocStatus; postingDate: string;
  sourceStoreId: string | null; sourceStore: string | null; targetStoreId: string | null; targetStore: string | null;
  remarks: string | null; totalValue: number; createdAt: string; submittedAt: string | null; cancelledAt: string | null;
  lines: StockEntryLine[];
}
export interface StockEntrySummary {
  id: string; number: string; type: StockEntryType; status: DocStatus; postingDate: string;
  sourceStore: string | null; targetStore: string | null; lineCount: number; totalValue: number; createdAt: string;
}
export interface UpsertStockEntry {
  type: StockEntryType; postingDate: string | null; sourceStoreId: string | null; targetStoreId: string | null;
  remarks: string | null; lines: { productId: string; quantity: number; rate: number | null }[];
}
export interface StockPositionRow {
  productId: string; sku: string; name: string; category: string | null; uom: string | null;
  storeId: string; store: string; quantity: number; valuationRate: number; stockValue: number;
}
export interface StockPosition extends PagedResult<StockPositionRow> { totalQuantity: number; totalValue: number }
export interface StockLedgerRow {
  id: number; postingDate: string; postedAt: string; productId: string; sku: string; productName: string;
  storeId: string; store: string; voucherType: string; voucherId: string | null; voucherNo: string; isReversal: boolean;
  qtyChange: number; qtyAfter: number; rate: number; valuationRate: number; valueChange: number; valueAfter: number;
}

/** Builds a query string from the non-empty values. */
export const qs = (params: Record<string, string | number | boolean | null | undefined>) => {
  const p = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) if (v !== null && v !== undefined && v !== '' && v !== false) p.set(k, String(v));
  const s = p.toString();
  return s ? `?${s}` : '';
};
export interface Order { id: string; customerId: string; status: string; totalAmount: number; orderedAt: string; items: unknown[] }

const json = (body: unknown) => JSON.stringify(body);

/** "Master › Users" for menu pages, "General" for top-level pages. */
export const sectionOf = (m: MenuPosition) => (m.module ? `${m.module} › ${m.group}` : 'General');

export const api = {
  login: (email: string, password: string) =>
    request<AuthResponse>('/api/auth/login', { method: 'POST', body: json({ email, password }) }),
  /** Ends the session on the server (revokes it and clears the refresh cookie). */
  logout: () => request<void>('/api/auth/logout', { method: 'POST' }),
  stats: () => request<DashboardStats>('/api/dashboard/stats'),
  analytics: (days: AnalyticsRange) => request<AnalyticsReport>(`/api/analytics?days=${days}`),
  chat: (question: string, history: ChatTurn[]) =>
    request<ChatResponse>('/ai/chat', { method: 'POST', body: json({ question, history }) }),

  myPermissions: () => request<MyPermissions>('/api/me/permissions'),

  customers: (page = 1) => request<PagedResult<Customer>>(`/api/customers?page=${page}&pageSize=25`),
  deleteCustomer: (id: string) => request<void>(`/api/customers/${id}`, { method: 'DELETE' }),
  products: (page = 1) => request<PagedResult<Product>>(`/api/products?page=${page}&pageSize=25`),
  deleteProduct: (id: string) => request<void>(`/api/products/${id}`, { method: 'DELETE' }),
  createProduct: (p: UpsertProduct) => request<Product>('/api/products', { method: 'POST', body: json(p) }),
  updateProduct: (id: string, p: UpsertProduct) => request<Product>(`/api/products/${id}`, { method: 'PUT', body: json(p) }),

  // Inventory masters (full lists for their own screens; lookups for everyone who can open an inventory screen)
  uoms: (all = true) => request<Uom[]>(`/api/uoms${qs({ all })}`),
  saveUom: (u: Omit<Uom, 'id'>, id?: string) =>
    request<Uom>(id ? `/api/uoms/${id}` : '/api/uoms', { method: id ? 'PUT' : 'POST', body: json(u) }),
  deleteUom: (id: string) => request<void>(`/api/uoms/${id}`, { method: 'DELETE' }),
  stores: (all = true) => request<Store[]>(`/api/stores${qs({ all })}`),
  saveStore: (s: Omit<Store, 'id'>, id?: string) =>
    request<Store>(id ? `/api/stores/${id}` : '/api/stores', { method: id ? 'PUT' : 'POST', body: json(s) }),
  deleteStore: (id: string) => request<void>(`/api/stores/${id}`, { method: 'DELETE' }),
  lookupStores: () => request<Store[]>('/api/stock/stores'),
  lookupUoms: () => request<Uom[]>('/api/stock/uoms'),
  lookupItems: (search = '') => request<ItemLookup[]>(`/api/stock/items${qs({ search })}`),

  // Stock entries: Draft -> Submitted -> Cancelled
  stockEntries: (f: { status?: string; type?: string; page?: number } = {}) =>
    request<PagedResult<StockEntrySummary>>(`/api/stock-entries${qs({ ...f, pageSize: 25 })}`),
  stockEntry: (id: string) => request<StockEntry>(`/api/stock-entries/${id}`),
  createStockEntry: (e: UpsertStockEntry) => request<StockEntry>('/api/stock-entries', { method: 'POST', body: json(e) }),
  updateStockEntry: (id: string, e: UpsertStockEntry) =>
    request<StockEntry>(`/api/stock-entries/${id}`, { method: 'PUT', body: json(e) }),
  deleteStockEntry: (id: string) => request<void>(`/api/stock-entries/${id}`, { method: 'DELETE' }),
  submitStockEntry: (id: string) => request<StockEntry>(`/api/stock-entries/${id}/submit`, { method: 'POST' }),
  cancelStockEntry: (id: string) => request<StockEntry>(`/api/stock-entries/${id}/cancel`, { method: 'POST' }),

  stockPosition: (f: { storeId?: string; search?: string; lowOnly?: boolean; page?: number } = {}) =>
    request<StockPosition>(`/api/stock/position${qs({ ...f, pageSize: 50 })}`),
  stockLedger: (f: { productId?: string; storeId?: string; from?: string; to?: string; page?: number } = {}) =>
    request<PagedResult<StockLedgerRow>>(`/api/stock/ledger${qs({ ...f, pageSize: 50 })}`),
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
