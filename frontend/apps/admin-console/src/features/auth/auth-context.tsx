import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
  useSyncExternalStore,
  type ReactNode,
} from 'react';
import { useQueryClient } from '@tanstack/react-query';
import type { ApiClient } from '../../lib/api/client';
import { isTripsStaff } from '../../lib/auth/claims';
import type { Session } from '../../lib/auth/session';
import type { SessionStore } from '../../lib/auth/session-store';
import { NotStaffError } from './sign-in-errors';

/**
 * `restoring` covers the moment after a page reload when we hold a refresh token but have not yet
 * traded it for a session. Treating that as "signed out" would bounce the reviewer to the sign-in
 * page for half a second on every reload.
 */
export type AuthStatus = 'restoring' | 'signed-in' | 'signed-out';

interface AuthContextValue {
  status: AuthStatus;
  session: Session | null;
  signIn(email: string, password: string): Promise<void>;
  signOut(): Promise<void>;
}

const AuthContext = createContext<AuthContextValue | null>(null);

export function AuthProvider({
  client,
  store,
  children,
}: {
  client: ApiClient;
  store: SessionStore;
  children: ReactNode;
}) {
  const queryClient = useQueryClient();
  const session = useSyncExternalStore(store.subscribe, store.getSession);

  const [restoring, setRestoring] = useState(
    () => store.getSession() === null && store.getRefreshToken() !== null,
  );

  useEffect(() => {
    if (!restoring) return;
    let active = true;

    // In development React runs effects twice. Both runs share one refresh, because the client
    // only ever has one in flight — see `refreshing` in lib/api/client.ts.
    client
      .restore()
      .catch(() => null)
      .finally(() => {
        if (active) setRestoring(false);
      });

    return () => {
      active = false;
    };
  }, [client, restoring]);

  // Whenever the person behind the session changes — sign-out, an expired session, a colleague
  // signing in at the same desk — throw away every cached answer. The cache holds agencies'
  // business details and documents; the next person must not see the last person's screens.
  const userId = session?.claims.userId ?? null;
  const previousUserId = useRef(userId);
  useEffect(() => {
    if (previousUserId.current !== userId) {
      queryClient.clear();
      previousUserId.current = userId;
    }
  }, [userId, queryClient]);

  const signIn = useCallback(
    async (email: string, password: string) => {
      const next = await client.signIn(email, password);

      // Checked BEFORE the session is stored, so an agency account never renders a single
      // screen of this console, not even for a frame.
      if (!isTripsStaff(next.claims)) {
        await client.revoke(next.refreshToken);
        throw new NotStaffError();
      }

      store.set(next);
    },
    [client, store],
  );

  const signOut = useCallback(() => client.signOut(), [client]);

  const value = useMemo<AuthContextValue>(
    () => ({
      status: restoring ? 'restoring' : session ? 'signed-in' : 'signed-out',
      session,
      signIn,
      signOut,
    }),
    [restoring, session, signIn, signOut],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const value = useContext(AuthContext);
  if (!value) throw new Error('useAuth must be used inside an <AuthProvider>.');
  return value;
}
