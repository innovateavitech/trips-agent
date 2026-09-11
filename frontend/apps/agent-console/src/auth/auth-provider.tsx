import { useQuery, useQueryClient } from '@tanstack/react-query';
import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useReducer,
  type ReactNode,
} from 'react';
import { setSessionExpiredHandler } from '../api/tokens';
import type { AgencyOption, AuthApi, Credentials, SessionUser } from './auth-api';
import { initialSessionState, sessionReducer, type SessionState } from './session';

interface AuthContextValue {
  session: SessionState;
  api: AuthApi;
  signIn: (credentials: Credentials) => Promise<void>;
  signOut: () => Promise<void>;
  switchAgency: (agencyId: string) => Promise<void>;
}

const AuthContext = createContext<AuthContextValue | null>(null);

/**
 * Owns the session: restores it on page load, and signs in, out and across
 * agencies.
 *
 * Every one of those clears the TanStack Query cache. That is not tidiness, it
 * is tenant isolation: the cache is keyed by what was asked (`['wallet']`), not
 * by who asked, so without clearing it the next agency — or the next person at
 * a shared agency desk — would be shown the previous one's balance and bookings
 * until each query happened to refetch.
 */
export function AuthProvider({ api, children }: { api: AuthApi; children: ReactNode }) {
  const queryClient = useQueryClient();
  const [session, dispatch] = useReducer(sessionReducer, initialSessionState);

  useEffect(() => {
    let cancelled = false;
    api.restore().then(
      (user) => {
        if (!cancelled) dispatch({ type: 'restored', user });
      },
      () => {
        if (!cancelled) dispatch({ type: 'restore-failed' });
      },
    );
    return () => {
      cancelled = true;
    };
  }, [api]);

  // The transport lives outside React. This is how it tells React that the
  // refresh token was refused and the session is over.
  useEffect(() => {
    setSessionExpiredHandler(() => {
      queryClient.clear();
      dispatch({ type: 'expired' });
    });
    return () => setSessionExpiredHandler(null);
  }, [queryClient]);

  const signIn = useCallback(
    async (credentials: Credentials) => {
      const user = await api.signIn(credentials);
      queryClient.clear();
      dispatch({ type: 'signed-in', user });
    },
    [api, queryClient],
  );

  const signOut = useCallback(async () => {
    await api.signOut();
    queryClient.clear();
    dispatch({ type: 'signed-out' });
  }, [api, queryClient]);

  const switchAgency = useCallback(
    async (agencyId: string) => {
      const user = await api.switchAgency(agencyId);
      queryClient.clear();
      dispatch({ type: 'agency-switched', user });
    },
    [api, queryClient],
  );

  const value = useMemo(
    () => ({ session, api, signIn, signOut, switchAgency }),
    [session, api, signIn, signOut, switchAgency],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const value = useContext(AuthContext);
  if (!value) throw new Error('useAuth must be used inside <AuthProvider>.');
  return value;
}

/**
 * The signed-in user. Only for components inside the authenticated route
 * group, where `RequireAuth` has already guaranteed there is one.
 */
export function useCurrentUser(): SessionUser {
  const { session } = useAuth();
  if (session.status !== 'signed-in') {
    throw new Error('useCurrentUser is only available inside the authenticated routes.');
  }
  return session.user;
}

/** The agencies the current user can switch between, current one included. */
export function useAgencies() {
  const { api } = useAuth();
  const user = useCurrentUser();

  return useQuery<AgencyOption[]>({
    queryKey: ['session', 'agencies', user.userId],
    queryFn: () => api.listAgencies(),
    staleTime: 5 * 60_000,
  });
}
