import { createApiClient } from '@trips/api-client';
import { getAccessToken, getRefreshToken, notifySessionExpired, storeTokenPair } from './tokens';

/**
 * ============================================================================
 *  Transparent token refresh — the reason nobody else has to think about 401s.
 * ============================================================================
 *
 * The access token lives fifteen minutes. An agent keeps the console open all
 * day. So a request WILL eventually go out with an expired token and come back
 * 401, and when it does this layer:
 *
 *   1. asks `POST /api/v1/auth/refresh` for a new pair, ONCE;
 *   2. sends the original request again with the new token;
 *   3. hands the caller the second answer, as if nothing had happened.
 *
 * Screens, hooks and queries never see the first 401.
 *
 * The part that matters most: ONE refresh at a time.
 *
 * The refresh token is single use, and the API (#16) treats a reused one as
 * theft — it revokes the whole token family and signs the agent out on every
 * device. A dashboard fires five queries at once; when the token expires they
 * all come back 401 together. Five parallel refreshes would spend the token
 * once and "reuse" it four times, and the agent would be thrown out for having
 * too many widgets. So every caller that needs a refresh shares one promise.
 */

export interface SessionTransportOptions {
  /** API origin, or `''` for this page's own origin through the dev proxy. */
  baseUrl: string;
  /** The network. Injected so tests can stand in for the API without a server. */
  fetch?: (request: Request) => Promise<Response>;
}

export interface SessionTransport {
  /** A `fetch` that authenticates, and refreshes and retries once on a 401. */
  authFetch: (request: Request) => Promise<Response>;
  /**
   * Makes sure there is a usable access token. Resolves `true` when one is
   * stored, `false` when the session is over or the API could not be reached.
   *
   * `failedToken` is the token a request was just rejected with. If a newer
   * one is already stored — another request refreshed first — this returns
   * straight away rather than refreshing a second time.
   */
  refresh: (failedToken: string | null) => Promise<boolean>;
}

export function createSessionTransport({
  baseUrl,
  fetch: send = (request) => globalThis.fetch(request),
}: SessionTransportOptions): SessionTransport {
  // Deliberately NOT authenticated: the refresh call carries its credential in
  // the body, and a 401 from it means "session over", never "refresh again".
  const unauthenticated = createApiClient({ baseUrl, fetch: send });

  let inFlight: Promise<boolean> | null = null;

  async function exchangeRefreshToken(failedToken: string | null): Promise<boolean> {
    const refreshToken = getRefreshToken();
    if (!refreshToken) {
      // A request was rejected and there is nothing to renew it with: the
      // session is over. With no failed token this is just "never signed in".
      if (failedToken !== null) notifySessionExpired();
      return false;
    }

    let result;
    try {
      result = await unauthenticated.POST('/api/v1/auth/refresh', { body: { refreshToken } });
    } catch {
      // The network, not the session. Keep the tokens: the agent is still
      // signed in, and the next request can try again once the line is back.
      return false;
    }

    if (result.data) {
      storeTokenPair(result.data);
      return true;
    }

    if (result.response.status === 401 || result.response.status === 400) {
      // Expired, revoked, or caught by reuse detection. Nothing in the browser
      // can recover from that — sign in again.
      notifySessionExpired();
    }
    // Anything else (a 500, a gateway timeout) is our problem, not theirs.
    // Keep the session and let the caller's error state say so.
    return false;
  }

  function refresh(failedToken: string | null): Promise<boolean> {
    const current = getAccessToken();
    if (current !== null && current !== failedToken) {
      return Promise.resolve(true);
    }

    if (!inFlight) {
      inFlight = exchangeRefreshToken(failedToken).finally(() => {
        inFlight = null;
      });
    }
    return inFlight;
  }

  function withBearer(request: Request, token: string | null): Request {
    const next = new Request(request);
    if (token) next.headers.set('Authorization', `Bearer ${token}`);
    return next;
  }

  async function authFetch(request: Request): Promise<Response> {
    // Cloned before the first send: a body can be read once, and a retried POST
    // must carry the same body as the original.
    const retryable = request.clone();

    // After a reload the access token is gone (it lives in memory) but the
    // refresh token survives. Renew first rather than sending a request we
    // already know will bounce.
    if (getAccessToken() === null && getRefreshToken() !== null) {
      await refresh(null);
    }

    const usedToken = getAccessToken();
    const response = await send(withBearer(request, usedToken));
    if (response.status !== 401) return response;

    const renewed = await refresh(usedToken);
    if (!renewed) return response;

    // Exactly one retry. If the fresh token is refused too, that answer is the
    // real one — refreshing again would only loop.
    return send(withBearer(retryable, getAccessToken()));
  }

  return { authFetch, refresh };
}
