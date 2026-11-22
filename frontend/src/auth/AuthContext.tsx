import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { api, isTwoFactorChallenge, setUnauthorizedHandler, storeSession, TOKEN_KEY, USER_KEY, type UserDto } from '../api/client';
import { forgetDeviceKeys } from '../chat/deviceKeys';
import { restoreLastPageAfterSignIn } from '../components/lastPage';

/** 'two-factor' = password accepted, now call login again with the authenticator (or recovery) code. */
export type LoginResult = 'signed-in' | 'two-factor';

interface AuthState {
  user: UserDto | null;
  isAuthenticated: boolean;
  /** `rememberDevice` (with a code): this browser skips the two-factor code for 30 days. */
  login: (email: string, password: string, code?: string, rememberDevice?: boolean) => Promise<LoginResult>;
  /** Signs out on the server (revokes the session) and locally. */
  logout: () => void;
  /** Shows a changed name/email (Settings) without signing in again. */
  updateUser: (changes: Partial<Pick<UserDto, 'fullName' | 'email'>>) => void;
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
    void forgetDeviceKeys(); // the unlocked secure-messaging key never outlives the session
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

  const login = useCallback(async (email: string, password: string, code?: string, rememberDevice = false): Promise<LoginResult> => {
    const res = await api.login(email, password, code, rememberDevice);
    if (isTwoFactorChallenge(res)) return 'two-factor';
    storeSession(res);
    restoreLastPageAfterSignIn(); // the first screen after this sign-in reopens where this user left off
    setUser(res.user);
    return 'signed-in';
  }, []);

  const updateUser = useCallback((changes: Partial<Pick<UserDto, 'fullName' | 'email'>>) => {
    setUser((u) => {
      if (!u) return u;
      const next = { ...u, ...changes };
      localStorage.setItem(USER_KEY, JSON.stringify(next));
      return next;
    });
  }, []);

  const value = useMemo(() => ({ user, isAuthenticated: !!user, login, logout, updateUser }),
    [user, login, logout, updateUser]);
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthState {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be used within AuthProvider');
  return ctx;
}
