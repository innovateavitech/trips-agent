/**
 * Where the session lives.
 *
 * ACCESS TOKEN — a module variable, and nowhere else. It is what every request
 * carries, it lives fifteen minutes, and nothing gains from it surviving a
 * reload: the refresh cookie can mint a new one in a single request. It is
 * never written to `localStorage` or `sessionStorage`.
 *
 * REFRESH TOKEN — not here at all. The API sets it as an `HttpOnly`, `Secure`,
 * `SameSite=Strict` cookie scoped to `/api/v1/auth` (issue 107), so no script on
 * this page can read it — including a script an XSS hole would let in. The
 * browser sends it to `/refresh` and `/logout` by itself; see `session-fetch.ts`.
 *
 * The console therefore cannot know whether it holds a session until it asks:
 * on a page load it simply tries to refresh, and a 401 means "signed out".
 */

/** Where an earlier version kept the refresh token. Removed on sight. */
const LEGACY_REFRESH_TOKEN_KEY = 'trips.agent-console.refresh-token';

let accessToken: string | null = null;

/* Storage can throw: Safari in private mode, or storage disabled by policy. */
function forgetLegacyRefreshToken(): void {
  try {
    window.sessionStorage.removeItem(LEGACY_REFRESH_TOKEN_KEY);
  } catch {
    // Nothing to clear if storage is unavailable.
  }
}

forgetLegacyRefreshToken();

export function getAccessToken(): string | null {
  return accessToken;
}

/**
 * Keeps a freshly issued access token. The refresh token that came with it is
 * already in the browser's cookie jar, where the API put it.
 */
export function storeAccessToken(pair: { accessToken: string }): void {
  accessToken = pair.accessToken;
}

export function clearTokens(): void {
  accessToken = null;
}

/** Registered by `AuthProvider`, so the transport can end the session without importing React. */
let onSessionExpired: (() => void) | null = null;

export function setSessionExpiredHandler(handler: (() => void) | null): void {
  onSessionExpired = handler;
}

/**
 * The refresh cookie was rejected — expired, revoked, or caught by reuse
 * detection. Nothing in the client can recover from that, so the session ends
 * and the auth guard sends the agent to sign in, remembering where they were.
 */
export function notifySessionExpired(): void {
  clearTokens();
  onSessionExpired?.();
}
