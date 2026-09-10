import { apiUrl } from './config';
import { setAccessToken } from './tokens';

/**
 * ============================================================================
 *  THE REFRESH CONTRACT — the one file to change when issue #16 lands.
 * ============================================================================
 *
 *  Issue #16 fixes the token LIFETIMES (access 15 min, refresh 30 days, single
 *  use, rotated, with reuse detection) but does not say how the refresh token
 *  reaches the server. Two options were on the table:
 *
 *    (a) httpOnly + Secure + SameSite cookie, set by the API   ← chosen
 *    (b) refresh token in the JSON body, stored by the client
 *
 *  (a) is chosen because (b) puts a 30-day credential somewhere JavaScript can
 *  read it, and #16's reuse detection then fires on an XSS theft instead of
 *  preventing it. With (a) the browser attaches the cookie and the client never
 *  holds the long-lived secret at all.
 *
 *  This is a CLIENT-SIDE ASSUMPTION about an endpoint nobody has built yet. It
 *  is flagged on the PR for #48. If #16 lands on (b), only this file changes —
 *  everything else goes through `refreshAccessToken()` and does not care.
 * ============================================================================
 */

interface RefreshResponse {
  accessToken: string;
  /** Seconds until expiry. Unused today; kept so proactive refresh is easy later. */
  expiresIn?: number;
}

export class RefreshFailedError extends Error {
  constructor(message = 'Session expired') {
    super(message);
    this.name = 'RefreshFailedError';
  }
}

/**
 * Trades the refresh cookie for a new access token, and stores it.
 *
 * Deliberately uses bare `fetch`, not `http()` — routing this through the
 * wrapper would let a 401 from the refresh endpoint trigger another refresh,
 * and that recurses until the stack gives out.
 */
export async function refreshAccessToken(): Promise<string> {
  const response = await fetch(apiUrl('/auth/refresh'), {
    method: 'POST',
    // Sends the httpOnly refresh cookie cross-origin. Without this the request
    // arrives with no credential and every refresh 401s.
    credentials: 'include',
    headers: { Accept: 'application/json' },
  });

  if (!response.ok) {
    throw new RefreshFailedError(`Refresh failed with ${response.status}`);
  }

  const body = (await response.json()) as RefreshResponse;
  if (!body.accessToken) {
    throw new RefreshFailedError('Refresh response contained no access token');
  }

  setAccessToken(body.accessToken);
  return body.accessToken;
}
