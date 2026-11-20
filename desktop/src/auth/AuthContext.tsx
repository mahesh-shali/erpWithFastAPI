import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { auth, setUnauthorizedHandler, type UserDto } from '../api/client';
import { forgetDeviceKeys } from '../chat/deviceKeys';

interface AuthState {
  user: UserDto | null;
  isAuthenticated: boolean;
  /** False while the app is resuming the previous session at startup. */
  ready: boolean;
  /** False when the OS has no secure key store: the session ends when the app closes. */
  persistent: boolean;
  login: (email: string, password: string) => Promise<void>;
  /** Signs out on the server (revokes the session) and locally. */
  logout: () => Promise<void>;
}

const AuthContext = createContext<AuthState | undefined>(undefined);

/**
 * `initialUser` skips the startup restore (tests). In the app it is undefined: on mount the main process
 * tries the stored refresh token and we either resume the session or show the login screen.
 */
export function AuthProvider({ children, initialUser }: { children: ReactNode; initialUser?: UserDto | null }) {
  const [user, setUser] = useState<UserDto | null>(initialUser ?? null);
  const [ready, setReady] = useState(initialUser !== undefined);
  const [persistent, setPersistent] = useState(true);

  useEffect(() => {
    if (initialUser !== undefined) return;
    let cancelled = false;
    auth.restore()
      .then((s) => {
        if (cancelled || !s) return;
        setUser(s.user);
        setPersistent(s.persistent);
      })
      .catch(() => undefined)
      .finally(() => { if (!cancelled) setReady(true); });
    return () => { cancelled = true; };
  }, [initialUser]);

  const clearLocal = useCallback(() => {
    void forgetDeviceKeys(); // the unlocked secure-messaging key never outlives the session
    setUser(null);
  }, []);

  useEffect(() => {
    setUnauthorizedHandler(clearLocal); // 401 even after the main process refreshed -> back to login
    const off = auth.onSignedOut(clearLocal); // session revoked / expired in the background
    return () => {
      setUnauthorizedHandler(null);
      off();
    };
  }, [clearLocal]);

  const logout = useCallback(async () => {
    clearLocal();
    await auth.logout().catch(() => undefined); // best effort: the session also expires on its own
  }, [clearLocal]);

  const login = useCallback(async (email: string, password: string) => {
    const s = await auth.login(email, password);
    setPersistent(s.persistent);
    setUser(s.user);
  }, []);

  const value = useMemo(
    () => ({ user, isAuthenticated: !!user, ready, persistent, login, logout }),
    [user, ready, persistent, login, logout],
  );
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthState {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be used within AuthProvider');
  return ctx;
}
