import type { Session } from './session';

/**
 * Holds the signed-in session and tells React when it changes.
 *
 * Where each token lives, and why:
 *
 * - The **access token** lives in memory only. It is the one sent with every request, and it dies
 *   with the tab. It is never written to `localStorage` or `sessionStorage`.
 * - The **refresh token** is not in this console at all. The API sets it as an `HttpOnly`, `Secure`,
 *   `SameSite=Strict` cookie (issue 107), which no script on this page can read — so an XSS hole
 *   cannot lift a thirty-day credential. The browser sends it back to `/auth/refresh` by itself,
 *   which is how reloading the page keeps the reviewer signed in.
 *
 * Shaped for `useSyncExternalStore`: `getSession` returns the same object until something
 * changes, which is what stops React re-rendering in a loop.
 */
export interface SessionStore {
  getSession(): Session | null;
  set(session: Session): void;
  clear(): void;
  subscribe(listener: () => void): () => void;
}

/** The subset of the Web Storage API this needs, so tests can pass a plain object. */
export type TokenStorage = Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>;

/** Where an earlier version kept the refresh token. Removed on sight. */
export const LEGACY_REFRESH_TOKEN_KEY = 'trips.admin.refreshToken';

export function createSessionStore(storage: TokenStorage | null): SessionStore {
  // Safari in private mode and some locked-down browsers throw on any storage access.
  try {
    storage?.removeItem(LEGACY_REFRESH_TOKEN_KEY);
  } catch {
    // Nothing to clear.
  }

  let session: Session | null = null;
  const listeners = new Set<() => void>();

  const notify = () => listeners.forEach((listener) => listener());

  return {
    getSession: () => session,
    set(next) {
      session = next;
      notify();
    },
    clear() {
      session = null;
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
