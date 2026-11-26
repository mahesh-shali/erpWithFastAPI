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
  role: 'SuperAdmin' | 'Admin' | 'Manager' | 'User';
  organizationId: string;
}
/**
 * What the renderer learns about a session. Tokens never reach the renderer: the main process keeps the
 * access token in memory and the refresh token in OS-encrypted storage.
 */
export interface SessionInfo { user: UserDto; sessionExpiresAt: string | null; persistent: boolean }
/** Login answer when the password was right but the account has two-factor sign-in: log in again with a code. */
export interface TwoFactorChallenge { twoFactorRequired: true }
export const isTwoFactorChallenge = (r: unknown): r is TwoFactorChallenge =>
  typeof r === 'object' && r !== null && (r as TwoFactorChallenge).twoFactorRequired === true;
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
  /** `code` = authenticator or recovery code; `rememberDevice` = this app skips the code for 30 days. */
  login: async (email: string, password: string, code?: string, rememberDevice = false) =>
    unwrap<SessionInfo | TwoFactorChallenge>(await window.erp.auth.login(email, password, code, rememberDevice)),
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
  twoFactorEnabled?: boolean;
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
export interface Customer {
  id: string; name: string; email: string | null; city: string | null; country: string | null;
  phone?: string | null; contactPerson?: string | null; taxId?: string | null; address?: string | null; state?: string | null;
  postalCode?: string | null; paymentTermsDays?: number;
}
export type UpsertCustomer = Omit<Customer, 'id'>;
export const ITEM_TYPES = ['Finished Good', 'Semi-Finished', 'Raw Material', 'Consumable'] as const;
export type ItemType = typeof ITEM_TYPES[number];
/** An item (page key "products"). unitPrice is the sales price. */
export interface Product {
  id: string; sku: string; name: string; category: string | null; unitPrice: number; stockQuantity: number;
  uomId?: string | null; uom?: string | null;
  itemType?: ItemType; description?: string | null; hsnCode?: string | null; taxRate?: number; standardCost?: number;
  reorderLevel?: number; leadTimeDays?: number; drawingNo?: string | null; isActive?: boolean;
}
export interface UpsertProduct {
  sku: string; name: string; category: string | null; unitPrice: number; stockQuantity: number; uomId: string | null;
  itemType?: ItemType; description?: string | null; hsnCode?: string | null; taxRate?: number; standardCost?: number;
  reorderLevel?: number; leadTimeDays?: number; drawingNo?: string | null; isActive?: boolean;
}

// ---------- Sales flow: Customer P.O. -> Work Order -> Sales Invoice ----------
export interface Company { name: string; address: string | null; state: string | null; phone: string | null; email: string | null; taxId: string | null }
export type PoStatus = 'Draft' | 'Confirmed' | 'Completed' | 'Cancelled';
/** A related document (a P.O.'s work orders and invoices, open P.O.s in pickers). */
export interface DocumentLink { id: string; number: string; status: string; date: string; amount: number; detail: string | null }
export interface CustomerPoLine {
  id: string; lineNo: number; productId: string; sku: string; productName: string; uom: string | null; hsnCode: string | null;
  quantity: number; rate: number; taxRate: number; amount: number; taxAmount: number;
  producedQty: number; invoicedQty: number; toProduce: number; toInvoice: number; inStock: number;
}
export interface CustomerPo {
  id: string; number: string; customerId: string; customerName: string; customerPoNo: string; poDate: string;
  deliveryDate: string | null; status: PoStatus; remarks: string | null; subTotal: number; taxTotal: number; grandTotal: number;
  createdAt: string; confirmedAt: string | null; completedAt: string | null; cancelledAt: string | null;
  lines: CustomerPoLine[]; workOrders: DocumentLink[]; invoices: DocumentLink[];
}
export interface CustomerPoSummary {
  id: string; number: string; customerName: string; customerPoNo: string; poDate: string; deliveryDate: string | null;
  status: PoStatus; lineCount: number; grandTotal: number; orderedQty: number; producedQty: number; invoicedQty: number; createdAt: string;
}
export interface UpsertCustomerPo {
  customerId: string; customerPoNo: string; poDate: string | null; deliveryDate: string | null; remarks: string | null;
  lines: { productId: string; quantity: number; rate: number; taxRate: number | null }[];
}
export interface WorkOrderMaterial { productId: string; sku: string; productName: string; uom: string | null; quantity: number; rate: number; amount: number; inStock: number }
export interface WorkOrder {
  id: string; number: string; status: DocStatus; postingDate: string;
  customerPoId: string | null; customerPoNumber: string | null; customerName: string | null; customerPoLineId: string | null;
  productId: string; sku: string; productName: string; uom: string | null; quantity: number;
  storeId: string; store: string; materialStoreId: string; materialStore: string;
  otherCost: number; materialCost: number; unitCost: number; totalCost: number; remarks: string | null;
  createdAt: string; submittedAt: string | null; cancelledAt: string | null; materials: WorkOrderMaterial[];
}
export interface WorkOrderSummary {
  id: string; number: string; status: DocStatus; postingDate: string; customerPoNumber: string | null; customerName: string | null;
  sku: string; productName: string; quantity: number; store: string; totalCost: number; createdAt: string;
}
export interface UpsertWorkOrder {
  customerPoLineId: string | null; productId: string | null; quantity: number; storeId: string; materialStoreId: string | null;
  postingDate: string | null; otherCost: number; remarks: string | null; materials: { productId: string; quantity: number }[];
}
export interface SalesInvoiceLine {
  lineNo: number; productId: string; sku: string; productName: string; uom: string | null; hsnCode: string | null;
  customerPoLineId: string | null; quantity: number; rate: number; discountPercent: number; amount: number; taxRate: number;
  taxAmount: number; total: number; costRate: number; inStock: number;
}
export interface TaxSummaryRow { taxRate: number; taxable: number; tax: number }
export interface SalesInvoice {
  id: string; number: string; status: DocStatus; invoiceDate: string; dueDate: string;
  customerId: string; customerName: string; customerPoId: string | null; customerPoNumber: string | null; customerPoRef: string | null;
  storeId: string; store: string; billingName: string; billingAddress: string | null; billingTaxId: string | null; billingState: string | null;
  shippingAddress: string | null; remarks: string | null;
  subTotal: number; discountTotal: number; taxTotal: number; roundOff: number; grandTotal: number; costOfGoods: number;
  interState: boolean; amountInWords: string; taxSummary: TaxSummaryRow[];
  createdAt: string; submittedAt: string | null; cancelledAt: string | null; lines: SalesInvoiceLine[];
}
export interface SalesInvoiceSummary {
  id: string; number: string; status: DocStatus; invoiceDate: string; dueDate: string; customerName: string;
  customerPoNumber: string | null; lineCount: number; grandTotal: number; createdAt: string;
}
export interface UpsertSalesInvoice {
  customerId: string | null; customerPoId: string | null; invoiceDate: string | null; dueDate: string | null; storeId: string;
  shippingAddress: string | null; remarks: string | null;
  lines: { customerPoLineId: string | null; productId: string | null; quantity: number; rate: number; discountPercent: number; taxRate: number | null }[];
}
/** template = the organization's own layout, rendered and sanitized on the server (null = built-in layout). */
export interface InvoicePrint { company: Company; invoice: SalesInvoice; template?: RenderedTemplate | null; templateWarning?: string | null }

// ---------- Print templates (Settings › Print templates) ----------
export type PrintDocumentType = 'sales-invoice';
/** html goes into a shadow root, css into a constructed stylesheet (both already sanitized by the API). */
export interface RenderedTemplate { templateId: string | null; name: string; version: number; html: string; css: string }
export interface PrintTemplateSummary {
  id: string; familyId: string; documentType: PrintDocumentType; name: string; version: number; isDefault: boolean;
  isArchived: boolean; createdAt: string;
}
export interface PrintTemplate extends PrintTemplateSummary { body: string }
export type StockStatus = 'OK' | 'Reorder' | 'Short';
export interface StockDetailsRow {
  productId: string; sku: string; name: string; itemType: string; category: string | null; uom: string | null;
  onHand: number; stockValue: number; committed: number; inProduction: number; available: number; reorderLevel: number; status: StockStatus;
}
export interface StoreBalance { storeId: string; store: string; quantity: number; valuationRate: number; stockValue: number }
export interface ItemCommitment {
  customerPoId: string; number: string; customerName: string; deliveryDate: string | null;
  ordered: number; produced: number; invoiced: number; pending: number;
}
export interface StockItemDetails {
  item: Product; summary: StockDetailsRow; stores: StoreBalance[]; commitments: ItemCommitment[];
  openWorkOrders: DocumentLink[]; recentMovements: StockLedgerRow[];
}

// ---------- Inventory ----------
export interface Uom { id: string; code: string; name: string; isActive: boolean }
export interface Store { id: string; code: string; name: string; location: string | null; isDefault: boolean; isActive: boolean }
export interface ItemLookup {
  id: string; sku: string; name: string; uom: string | null; stockQuantity: number; unitPrice: number;
  itemType?: string; taxRate?: number; hsnCode?: string | null;
}
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
  /** The caller's signed-in devices (desktop apps and browsers). */
  sessions: () => request<SessionDto[]>('/api/auth/sessions'),
  revokeSession: (id: string) => request<void>(`/api/auth/sessions/${id}`, { method: 'DELETE' }),
  logoutOthers: () => request<{ revoked: number }>('/api/auth/logout-others', { method: 'POST' }),
  stats: () => request<DashboardStats>('/api/dashboard/stats'),
  analytics: (days: AnalyticsRange) => request<AnalyticsReport>(`/api/analytics?days=${days}`),
  chat: (question: string, history: ChatTurn[]) =>
    request<ChatResponse>('/api/ai/chat', { method: 'POST', body: json({ question, history }) }),

  myPermissions: () => request<MyPermissions>('/api/me/permissions'),

  customers: (page = 1, search = '') =>
    request<PagedResult<Customer>>(`/api/customers?page=${page}&pageSize=25${qs({ search }).replace('?', '&')}`),
  deleteCustomer: (id: string) => request<void>(`/api/customers/${id}`, { method: 'DELETE' }),
  saveCustomer: (c: UpsertCustomer, id?: string) =>
    request<Customer>(id ? `/api/customers/${id}` : '/api/customers', { method: id ? 'PUT' : 'POST', body: json(c) }),
  products: (page = 1, f: { search?: string; itemType?: string } = {}) =>
    request<PagedResult<Product>>(`/api/products?page=${page}&pageSize=25${qs(f).replace('?', '&')}`),
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

  // Sales flow
  company: () => request<Company>('/api/company'),
  saveCompany: (c: Company) => request<Company>('/api/company', { method: 'PUT', body: json(c) }),
  lookupCustomers: () => request<Customer[]>('/api/sales/lookups/customers'),
  lookupOpenPos: (customerId = '') => request<DocumentLink[]>(`/api/sales/lookups/open-pos${qs({ customerId })}`),

  customerPos: (f: { status?: string; search?: string; page?: number } = {}) =>
    request<PagedResult<CustomerPoSummary>>(`/api/customer-pos${qs({ ...f, pageSize: 25 })}`),
  customerPo: (id: string) => request<CustomerPo>(`/api/customer-pos/${id}`),
  createCustomerPo: (p: UpsertCustomerPo) => request<CustomerPo>('/api/customer-pos', { method: 'POST', body: json(p) }),
  updateCustomerPo: (id: string, p: UpsertCustomerPo) => request<CustomerPo>(`/api/customer-pos/${id}`, { method: 'PUT', body: json(p) }),
  deleteCustomerPo: (id: string) => request<void>(`/api/customer-pos/${id}`, { method: 'DELETE' }),
  confirmCustomerPo: (id: string) => request<CustomerPo>(`/api/customer-pos/${id}/confirm`, { method: 'POST' }),
  cancelCustomerPo: (id: string) => request<CustomerPo>(`/api/customer-pos/${id}/cancel`, { method: 'POST' }),

  workOrders: (f: { status?: string; search?: string; page?: number } = {}) =>
    request<PagedResult<WorkOrderSummary>>(`/api/work-orders${qs({ ...f, pageSize: 25 })}`),
  workOrder: (id: string) => request<WorkOrder>(`/api/work-orders/${id}`),
  createWorkOrder: (w: UpsertWorkOrder) => request<WorkOrder>('/api/work-orders', { method: 'POST', body: json(w) }),
  updateWorkOrder: (id: string, w: UpsertWorkOrder) => request<WorkOrder>(`/api/work-orders/${id}`, { method: 'PUT', body: json(w) }),
  deleteWorkOrder: (id: string) => request<void>(`/api/work-orders/${id}`, { method: 'DELETE' }),
  submitWorkOrder: (id: string) => request<WorkOrder>(`/api/work-orders/${id}/submit`, { method: 'POST' }),
  cancelWorkOrder: (id: string) => request<WorkOrder>(`/api/work-orders/${id}/cancel`, { method: 'POST' }),

  salesInvoices: (f: { status?: string; search?: string; page?: number } = {}) =>
    request<PagedResult<SalesInvoiceSummary>>(`/api/sales-invoices${qs({ ...f, pageSize: 25 })}`),
  salesInvoice: (id: string) => request<SalesInvoice>(`/api/sales-invoices/${id}`),
  invoicePrint: (id: string) => request<InvoicePrint>(`/api/sales-invoices/${id}/print`),
  createSalesInvoice: (i: UpsertSalesInvoice) => request<SalesInvoice>('/api/sales-invoices', { method: 'POST', body: json(i) }),
  updateSalesInvoice: (id: string, i: UpsertSalesInvoice) =>
    request<SalesInvoice>(`/api/sales-invoices/${id}`, { method: 'PUT', body: json(i) }),
  deleteSalesInvoice: (id: string) => request<void>(`/api/sales-invoices/${id}`, { method: 'DELETE' }),
  submitSalesInvoice: (id: string) => request<SalesInvoice>(`/api/sales-invoices/${id}/submit`, { method: 'POST' }),
  cancelSalesInvoice: (id: string) => request<SalesInvoice>(`/api/sales-invoices/${id}/cancel`, { method: 'POST' }),

  printTemplates: (documentType: PrintDocumentType = 'sales-invoice') =>
    request<PrintTemplateSummary[]>(`/api/print-templates${qs({ documentType })}`),
  printTemplate: (id: string) => request<PrintTemplate>(`/api/print-templates/${id}`),
  printTemplateStarter: (documentType: PrintDocumentType = 'sales-invoice') =>
    request<{ documentType: PrintDocumentType; body: string }>(`/api/print-templates/starter${qs({ documentType })}`),
  createPrintTemplate: (t: { documentType: PrintDocumentType; name: string; body: string; makeDefault: boolean }) =>
    request<PrintTemplate>('/api/print-templates', { method: 'POST', body: json(t) }),
  /** Saves a new version (the result has a new id). */
  updatePrintTemplate: (id: string, t: { name: string; body: string }) =>
    request<PrintTemplate>(`/api/print-templates/${id}`, { method: 'PUT', body: json(t) }),
  setDefaultPrintTemplate: (id: string) => request<PrintTemplate>(`/api/print-templates/${id}/default`, { method: 'POST' }),
  useBuiltInPrint: (documentType: PrintDocumentType = 'sales-invoice') =>
    request<void>(`/api/print-templates/use-built-in${qs({ documentType })}`, { method: 'POST' }),
  archivePrintTemplate: (id: string) => request<void>(`/api/print-templates/${id}`, { method: 'DELETE' }),
  previewPrintTemplate: (p: { documentType: PrintDocumentType; body: string; invoiceId: string | null }) =>
    request<RenderedTemplate>('/api/print-templates/preview', { method: 'POST', body: json(p) }),

  stockDetails: (f: { search?: string; itemType?: string; attention?: boolean; page?: number } = {}) =>
    request<PagedResult<StockDetailsRow>>(`/api/stock/details${qs({ ...f, pageSize: 50 })}`),
  stockItemDetails: (productId: string) => request<StockItemDetails>(`/api/stock/details/${productId}`),

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
