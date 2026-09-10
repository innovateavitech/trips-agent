import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
} from 'react';
import type { ReactNode } from 'react';
import { http, resetRefreshState } from '../api/http';
import { refreshAccessToken } from '../api/refresh';
import { clearAccessToken, setSessionExpiredHandler } from '../api/tokens';
import type { AgencyRef, Session } from './types';

export type AuthStatus = 'bootstrapping' | 'authenticated' | 'anonymous';

interface AuthContextValue {
  status: AuthStatus;
  session: Session | null;
  /** Replaces the session after the login screen (#49) authenticates. */
  signIn: (session: Session) => void;
  signOut: () => Promise<void>;
  switchAgency: (agencyId: string) => void;
}

const AuthContext = createContext<AuthContextValue | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [status, setStatus] = useState<AuthStatus>('bootstrapping');
  const [session, setSession] = useState<Session | null>(null);

  /* StrictMode double-invokes effects in development. Without this guard the
     bootstrap spends the single-use refresh token twice and #16's reuse
     detection revokes the chain — a bug that only ever appears in dev. */
  const bootstrapped = useRef(false);

  const endSession = useCallback(() => {
    clearAccessToken();
    resetRefreshState();
    setSession(null);
    setStatus('anonymous');
  }, []);

  /* Lets the http layer tear the session down when a refresh is rejected,
     without the api/ modules having to import React. */
  useEffect(() => {
    setSessionExpiredHandler(endSession);
    return () => setSessionExpiredHandler(null);
  }, [endSession]);

  /**
   * Silent bootstrap.
   *
   * The access token lives in memory, so a page reload starts with nothing. If
   * the refresh cookie is still valid the agent is signed in already and should
   * not be shown a login screen — so we spend one refresh, then load the
   * session. If it fails, they are simply anonymous. That is not an error and
   * must not be reported as one.
   */
  useEffect(() => {
    if (bootstrapped.current) return;
    bootstrapped.current = true;

    let cancelled = false;

    void (async () => {
      try {
        await refreshAccessToken();
        const me = await http<Session>('/auth/me');
        if (!cancelled) {
          setSession(me);
          setStatus('authenticated');
        }
      } catch {
        if (!cancelled) {
          clearAccessToken();
          setStatus('anonymous');
        }
      }
    })();

    return () => {
      cancelled = true;
    };
  }, []);

  const signIn = useCallback((next: Session) => {
    setSession(next);
    setStatus('authenticated');
  }, []);

  const signOut = useCallback(async () => {
    try {
      // Best effort: #16 revokes the refresh token server-side. If the network
      // is down we still drop the local session — never leave someone looking
      // signed in on a shared machine because a request failed.
      await http('/auth/logout', { method: 'POST' });
    } catch {
      // Intentionally ignored.
    } finally {
      endSession();
    }
  }, [endSession]);

  /**
   * Switches which agency the console is showing.
   *
   * Client-side only for now. Once #16 lands this must also re-issue the access
   * token so its `agency_id` claim matches, otherwise the UI shows agency B
   * while the API still answers for agency A. The cache reset that belongs with
   * it lives in `AgencySwitcher`.
   */
  const switchAgency = useCallback((agencyId: string) => {
    setSession((current) => {
      if (!current) return current;
      const target = current.agencies.find((a: AgencyRef) => a.id === agencyId);
      return target ? { ...current, activeAgency: target } : current;
    });
  }, []);

  const value = useMemo<AuthContextValue>(
    () => ({ status, session, signIn, signOut, switchAgency }),
    [status, session, signIn, signOut, switchAgency],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const context = useContext(AuthContext);
  if (!context) {
    throw new Error('useAuth must be used inside <AuthProvider>');
  }
  return context;
}
