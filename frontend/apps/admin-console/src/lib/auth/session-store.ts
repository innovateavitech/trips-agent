import type { Session } from './session';

/**
 * Holds the signed-in session and tells React when it changes.
 *
 * Where each token lives, and why:
 *
 * - The **access token** lives in memory only. It is the one sent with every request, and it dies
 *   with the tab.
 * - The **refresh token** is also kept in `sessionStorage`, so reloading the page does not sign
 *   the reviewer out. `sessionStorage` is per tab and is emptied when the tab closes, so a shared
 *   office machine does not stay signed in overnight.
 *
 * The honest trade-off: script running on this page could read `sessionStorage`. The fix is an
 * httpOnly cookie the page cannot read at all, which needs the API to issue one — that is the
 * session-hardening piece of epic #66, alongside the cookie work in issue 107.
 *
 * Shaped for `useSyncExternalStore`: `getSession` returns the same object until something
 * changes, which is what stops React re-rendering in a loop.
 */
export interface SessionStore {
  getSession(): Session | null;
  /** The refresh token, including one left over from before a page reload. */
  getRefreshToken(): string | null;
  set(session: Session): void;
  clear(): void;
  subscribe(listener: () => void): () => void;
}

/** The subset of the Web Storage API this needs, so tests can pass a plain object. */
export type TokenStorage = Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>;

const REFRESH_TOKEN_KEY = 'trips.admin.refreshToken';

export function createSessionStore(storage: TokenStorage | null): SessionStore {
  let session: Session | null = null;
  const listeners = new Set<() => void>();

  const notify = () => listeners.forEach((listener) => listener());

  // Every storage call is guarded: Safari in private mode and some locked-down browsers throw on
  // access. Losing "stay signed in across a reload" is fine; crashing the console is not.
  const readStored = (): string | null => {
    try {
      return storage?.getItem(REFRESH_TOKEN_KEY) ?? null;
    } catch {
      return null;
    }
  };

  const writeStored = (value: string | null) => {
    try {
      if (value === null) storage?.removeItem(REFRESH_TOKEN_KEY);
      else storage?.setItem(REFRESH_TOKEN_KEY, value);
    } catch {
      // See above — keep going without persistence.
    }
  };

  return {
    getSession: () => session,
    getRefreshToken: () => session?.refreshToken ?? readStored(),
    set(next) {
      session = next;
      writeStored(next.refreshToken);
      notify();
    },
    clear() {
      session = null;
      writeStored(null);
      notify();
    },
    subscribe(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
  };
}

/** `window.sessionStorage`, or `null` where touching it throws. */
export function browserSessionStorage(): TokenStorage | null {
  try {
    return window.sessionStorage;
  } catch {
    return null;
  }
}
