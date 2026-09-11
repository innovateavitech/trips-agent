/**
 * Where the two tokens from `POST /api/v1/auth/login` live.
 *
 * ACCESS TOKEN — a module variable, and nowhere else. It is what every request
 * carries, it lives fifteen minutes, and nothing gains from it surviving a
 * reload: the refresh token can mint a new one in a single request.
 *
 * REFRESH TOKEN — `sessionStorage`. The API (#16) returns it in the JSON body
 * and expects it back in the body, so the browser cannot hold it in an httpOnly
 * cookie for us; this app has to keep it somewhere.
 *
 *   - Memory only would sign the agent out on every F5, which on a console used
 *     all day is not a real option.
 *   - `localStorage` keeps a 30-day credential readable by script on disk
 *     indefinitely and shares it between tabs. Two tabs refreshing with the same
 *     single-use token trip #16's reuse detection, which revokes the whole chain
 *     and signs the agent out everywhere.
 *   - `sessionStorage` survives a reload, dies with the tab, and is not shared
 *     between tabs. It is the most conservative choice that still works.
 *
 * It is still readable by script, so an XSS hole could lift it. The durable fix
 * is an httpOnly cookie set by the API; that is a backend change, and when it
 * lands only this file and `refresh.ts` change. Recorded as an assumption on
 * issue #48.
 */

/** Exported for tests that need to recreate what a page reload leaves behind. */
export const REFRESH_TOKEN_KEY = 'trips.agent-console.refresh-token';

let accessToken: string | null = null;

/* Storage can throw: Safari in private mode, or storage disabled by policy.
   Every access is guarded so that failure means "not signed in", never a crash. */
function storage(): Storage | null {
  try {
    return window.sessionStorage;
  } catch {
    return null;
  }
}

export function getAccessToken(): string | null {
  return accessToken;
}

export function getRefreshToken(): string | null {
  try {
    return storage()?.getItem(REFRESH_TOKEN_KEY) ?? null;
  } catch {
    return null;
  }
}

/**
 * Stores a freshly issued pair. Both tokens are replaced together: the refresh
 * token is single use, so after a refresh the old one is already dead and
 * keeping it would only guarantee a reuse-detection sign-out later.
 */
export function storeTokenPair(pair: { accessToken: string; refreshToken: string }): void {
  accessToken = pair.accessToken;
  try {
    storage()?.setItem(REFRESH_TOKEN_KEY, pair.refreshToken);
  } catch {
    // Storage full or blocked. The session still works until the tab reloads.
  }
}

export function clearTokens(): void {
  accessToken = null;
  try {
    storage()?.removeItem(REFRESH_TOKEN_KEY);
  } catch {
    // Nothing to clear if storage is unavailable.
  }
}

/** Registered by `AuthProvider`, so the transport can end the session without importing React. */
let onSessionExpired: (() => void) | null = null;

export function setSessionExpiredHandler(handler: (() => void) | null): void {
  onSessionExpired = handler;
}

/**
 * The refresh token was rejected — expired, revoked, or caught by reuse
 * detection. Nothing in the client can recover from that, so the session ends
 * and the auth guard sends the agent to sign in, remembering where they were.
 */
export function notifySessionExpired(): void {
  clearTokens();
  onSessionExpired?.();
}
