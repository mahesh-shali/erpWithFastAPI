import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { api, setUnauthorizedHandler, storeSession, TOKEN_KEY, USER_KEY, type UserDto } from '../api/client';

interface AuthState {
  user: UserDto | null;
  isAuthenticated: boolean;
  login: (email: string, password: string) => Promise<void>;
  /** Signs out on the server (revokes the session) and locally. */
  logout: () => void;
}

const AuthContext = createContext<AuthState | undefined>(undefined);

function loadUser(): UserDto | null {
  try {
    const raw = localStorage.getItem(USER_KEY);
    return raw && localStorage.getItem(TOKEN_KEY) ? (JSON.parse(raw) as UserDto) : null;
  } catch {
    return null;
  }
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<UserDto | null>(loadUser);

  const clearLocal = useCallback(() => {
    localStorage.removeItem(TOKEN_KEY);
    localStorage.removeItem(USER_KEY);
    setUser(null);
  }, []);

  const logout = useCallback(() => {
    api.logout().catch(() => undefined); // best effort: the session also expires on its own
    clearLocal();
  }, [clearLocal]);

  useEffect(() => {
    setUnauthorizedHandler(clearLocal); // 401 even after a refresh attempt -> back to login
    return () => setUnauthorizedHandler(null);
  }, [clearLocal]);

  const login = useCallback(async (email: string, password: string) => {
    const res = await api.login(email, password);
    storeSession(res);
    setUser(res.user);
  }, []);

  const value = useMemo(() => ({ user, isAuthenticated: !!user, login, logout }), [user, login, logout]);
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthState {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be used within AuthProvider');
  return ctx;
}
