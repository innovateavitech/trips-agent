import { readClaims, type StaffClaims } from './claims';

/**
 * What `/auth/login` and `/auth/refresh` return — TokenPairResponse in TripsAgent.Contracts. The
 * refresh token is not in it: it arrives as an HttpOnly cookie this code never sees.
 */
export interface TokenPairResponse {
  accessToken: string;
  expiresInSeconds: number;
}

export interface Session {
  accessToken: string;
  /** Epoch milliseconds, on the browser's clock. */
  accessExpiresAt: number;
  claims: StaffClaims;
}

/**
 * Refresh this long before the access token expires, so a request is not sent with a token that
 * dies in flight. The API allows no clock skew at all (ClockSkew = 0 in Program.cs).
 */
export const REFRESH_AHEAD_MS = 30_000;

/**
 * Builds a session from a freshly issued pair.
 *
 * The expiry comes from `expiresInSeconds`, counted from now, and not from the token's `exp`
 * claim. `exp` is on the server's clock; a laptop running five minutes fast would read a
 * perfectly good token as expired and refresh on every single request.
 */
export function sessionFromTokens(pair: TokenPairResponse, now: number): Session {
  const claims = readClaims(pair.accessToken);
  if (!claims) {
    throw new Error('The server issued an access token this console cannot read.');
  }

  return {
    accessToken: pair.accessToken,
    accessExpiresAt: now + pair.expiresInSeconds * 1000,
    claims,
  };
}

export function needsRefresh(session: Session, now: number): boolean {
  return session.accessExpiresAt - now <= REFRESH_AHEAD_MS;
}
