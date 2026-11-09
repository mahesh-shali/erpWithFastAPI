import { Navigate, Route, Routes } from 'react-router-dom';
import { PermissionsProvider, RequirePage } from './auth/PermissionsContext';
import { PrivateRoute } from './auth/PrivateRoute';
import { Layout } from './components/Layout';
import { ComingSoonRoute } from './pages/ComingSoonPage';
import { DashboardPage } from './pages/DashboardPage';
import { AssistantPage, CustomersPage, OrdersPage, ProductsPage } from './pages/DataPages';
import { LoginPage } from './pages/LoginPage';
import { PagesPage } from './pages/master/PagesPage';
import { RoleRightsPage } from './pages/master/RoleRightsPage';
import { UserRightsPage } from './pages/master/UserRightsPage';
import { UsersPage } from './pages/master/UsersPage';

/**
 * Routes match the `route` of each page in the Pages master; RequirePage checks canRead (the API re-checks).
 * Pages without a screen yet fall through to ComingSoonRoute.
 */
export default function App() {
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route element={<PrivateRoute><PermissionsProvider><Layout /></PermissionsProvider></PrivateRoute>}>
        <Route index element={<RequirePage page="dashboard"><DashboardPage /></RequirePage>} />
        <Route path="assistant" element={<RequirePage page="ai-chat"><AssistantPage /></RequirePage>} />
        <Route path="master/users" element={<RequirePage page="master-users"><UsersPage /></RequirePage>} />
        <Route path="master/user-rights" element={<RequirePage page="master-user-rights"><UserRightsPage /></RequirePage>} />
        <Route path="master/role-rights" element={<RequirePage page="master-role-rights"><RoleRightsPage /></RequirePage>} />
        <Route path="master/customers" element={<RequirePage page="customers"><CustomersPage /></RequirePage>} />
        <Route path="master/products" element={<RequirePage page="products"><ProductsPage /></RequirePage>} />
        <Route path="master/pages" element={<RequirePage page="master-pages"><PagesPage /></RequirePage>} />
        <Route path="sales/orders" element={<RequirePage page="orders"><OrdersPage /></RequirePage>} />
        <Route path="*" element={<ComingSoonRoute />} />
      </Route>
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  );
}
