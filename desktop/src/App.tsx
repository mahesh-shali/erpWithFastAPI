import { Navigate, Route, Routes } from 'react-router-dom';
import { PermissionsProvider, RequirePage } from './auth/PermissionsContext';
import { PrivateRoute } from './auth/PrivateRoute';
import { Layout } from './components/Layout';
import { AnalyticsPage } from './pages/AnalyticsPage';
import { DevicesPage } from './pages/account/DevicesPage';
import { ComingSoonRoute } from './pages/ComingSoonPage';
import { StoresPage, UomsPage } from './pages/inventory/MasterPages';
import { StockEntriesPage, StockEntryPage } from './pages/inventory/StockEntryPages';
import { StockLedgerPage, StockPositionPage } from './pages/inventory/StockReportPages';
import { StockDetailsPage, StockItemDetailsPage } from './pages/inventory/StockDetailsPages';
import { WorkOrderPage, WorkOrdersPage } from './pages/production/WorkOrderPages';
import { CustomerPoPage, CustomerPosPage } from './pages/sales/CustomerPoPages';
import { InvoicePrintPage, SalesInvoicePage, SalesInvoicesPage } from './pages/sales/InvoicePages';
import { DashboardPage } from './pages/DashboardPage';
import { AssistantPage, CustomersPage, OrdersPage, ProductsPage } from './pages/DataPages';
import { LoginPage } from './pages/LoginPage';
import { MessagesPage } from './pages/MessagesPage';
import { RoleRightsPage } from './pages/master/RoleRightsPage';
import { UserRightsPage } from './pages/master/UserRightsPage';
import { UsersPage } from './pages/master/UsersPage';
import { SettingsPage } from './pages/settings/SettingsPage';

/**
 * Flat URLs used by V.SMART ERP (and old bookmarks) redirect to their place in this app's menu,
 * so users coming from that system can keep typing the paths they know.
 */
const LEGACY_ALIASES: [string, string][] = [
  ['dashboard', '/'],
  ['user', '/master/users'],
  ['userrights', '/master/user-rights'],
  ['customer', '/master/customers'],
  ['vendor', '/master/vendors'],
  ['uom', '/master/uom'],
  ['stores', '/master/stores'],
  ['production/plan', '/planning/production-plan'],
  // Products became Items (Master › Inventory › Items)
  ['master/products', '/master/items'],
];

/**
 * Routes match the `route` of each page in the Pages master; RequirePage checks canRead (the API re-checks).
 * Pages without a screen yet fall through to ComingSoonRoute.
 */
export default function App() {
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      {/* Print layout: no menu or top bar, so the page prints as a clean A4 invoice. */}
      <Route path="/sales/invoices/:id/print" element={
        <PrivateRoute><PermissionsProvider><RequirePage page="sales-invoices"><InvoicePrintPage /></RequirePage></PermissionsProvider></PrivateRoute>
      } />
      <Route element={<PrivateRoute><PermissionsProvider><Layout /></PermissionsProvider></PrivateRoute>}>
        <Route index element={<RequirePage page="dashboard"><DashboardPage /></RequirePage>} />
        <Route path="analytics" element={<RequirePage page="analytics"><AnalyticsPage /></RequirePage>} />
        <Route path="assistant" element={<RequirePage page="ai-chat"><AssistantPage /></RequirePage>} />
        <Route path="messages" element={<RequirePage page="messages"><MessagesPage /></RequirePage>} />
        {/* Own account settings: every signed-in user. The Users tab checks "settings-users" itself. */}
        <Route path="settings/:tab?" element={<SettingsPage />} />
        <Route path="account/devices" element={<DevicesPage />} />
        <Route path="master/users" element={<RequirePage page="master-users"><UsersPage /></RequirePage>} />
        <Route path="master/user-rights" element={<RequirePage page="master-user-rights"><UserRightsPage /></RequirePage>} />
        <Route path="master/role-rights" element={<RequirePage page="master-role-rights"><RoleRightsPage /></RequirePage>} />
        <Route path="master/customers" element={<RequirePage page="customers"><CustomersPage /></RequirePage>} />
        <Route path="master/items" element={<RequirePage page="products"><ProductsPage /></RequirePage>} />
        {/* The Pages master moved into Settings; old links and bookmarks land on its tab. */}
        <Route path="master/pages" element={<Navigate to="/settings/pages" replace />} />
        <Route path="sales/orders" element={<RequirePage page="orders"><OrdersPage /></RequirePage>} />
        {/* Sales flow: Customer P.O. -> Work Order -> Sales Invoice */}
        <Route path="sales/customer-po" element={<RequirePage page="customer-pos"><CustomerPosPage /></RequirePage>} />
        <Route path="sales/customer-po/:id" element={<RequirePage page="customer-pos"><CustomerPoPage /></RequirePage>} />
        <Route path="production/work-orders" element={<RequirePage page="work-orders"><WorkOrdersPage /></RequirePage>} />
        <Route path="production/work-orders/:id" element={<RequirePage page="work-orders"><WorkOrderPage /></RequirePage>} />
        <Route path="sales/invoices" element={<RequirePage page="sales-invoices"><SalesInvoicesPage /></RequirePage>} />
        <Route path="sales/invoices/:id" element={<RequirePage page="sales-invoices"><SalesInvoicePage /></RequirePage>} />
        {/* Inventory */}
        <Route path="master/uom" element={<RequirePage page="uom"><UomsPage /></RequirePage>} />
        <Route path="master/stores" element={<RequirePage page="stores"><StoresPage /></RequirePage>} />
        <Route path="inventory/stock-addition" element={<RequirePage page="stock-addition"><StockEntriesPage /></RequirePage>} />
        <Route path="inventory/stock-addition/:id" element={<RequirePage page="stock-addition"><StockEntryPage /></RequirePage>} />
        <Route path="inventory/stock-position" element={<RequirePage page="stock-position"><StockPositionPage /></RequirePage>} />
        <Route path="inventory/stock-details" element={<RequirePage page="stock-details"><StockDetailsPage /></RequirePage>} />
        <Route path="inventory/stock-details/:productId" element={<RequirePage page="stock-details"><StockItemDetailsPage /></RequirePage>} />
        <Route path="reports/stock" element={<RequirePage page="stock-report"><StockLedgerPage /></RequirePage>} />
        {LEGACY_ALIASES.map(([from, to]) => (
          <Route key={from} path={from} element={<Navigate to={to} replace />} />
        ))}
        <Route path="*" element={<ComingSoonRoute />} />
      </Route>
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  );
}
