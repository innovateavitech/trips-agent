/**
 * Where the access token lives: a module variable, and nowhere else.
 *
 * NOT `localStorage`. Anything in `localStorage` is readable by any script that
 * manages to run on the page, so a single XSS hole becomes "attacker holds a
 * valid agent session against a product that moves money". A module variable
 * dies with the tab, which is the behaviour we want.
 *
 * The cost is that a refresh (F5) starts with no access token. That is handled
 * by the silent bootstrap in `auth-context.tsx`, which trades the long-lived
 * refresh credential for a new access token before the app renders.
 */

let accessToken: string | null = null;

/** Registered by the auth provider so a failed refresh can tear the session down. */
let onSessionExpired: (() => void) | null = null;

export function getAccessToken(): string | null {
  return accessToken;
}

export function setAccessToken(token: string | null): void {
  accessToken = token;
}

export function clearAccessToken(): void {
  accessToken = null;
}

export function setSessionExpiredHandler(handler: (() => void) | null): void {
  onSessionExpired = handler;
}

/**
 * Called when the refresh token is rejected — expired, revoked, or caught by
 * #16's reuse detection. There is no recovering from this in the client; the
 * only correct move is back to the login screen.
 */
export function notifySessionExpired(): void {
  clearAccessToken();
  onSessionExpired?.();
}
