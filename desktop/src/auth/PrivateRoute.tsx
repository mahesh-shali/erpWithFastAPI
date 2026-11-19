import type { ReactNode } from 'react';
import { Navigate, useLocation } from 'react-router-dom';
import { useAuth } from './AuthContext';

export function PrivateRoute({ children, role }: { children: ReactNode; role?: 'SuperAdmin' | 'Admin' | 'User' }) {
  const { user, ready } = useAuth();
  const location = useLocation();
  if (!ready) return <p className="hint splash" role="status">Restoring your session…</p>;
  if (!user) return <Navigate to="/login" replace state={{ from: location }} />;
  if (role && user.role !== role) return <Navigate to="/" replace />;
  return <>{children}</>;
}
