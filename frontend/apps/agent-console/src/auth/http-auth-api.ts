import type { ApiClient, Schemas } from '@trips/api-client';
import { unwrap } from '../api/errors';
import { clearTokens, storeAccessToken } from '../api/tokens';
import type { AuthApi, SessionUser } from './auth-api';

/**
 * The real session, against `/api/v1/auth` (#16).
 *
 *   sign in  → POST /login   (unauthenticated client) → keep the access token,
 *                             the API sets the refresh cookie → GET /me
 *   restore  → GET /me       (authenticated client — `authFetch` refreshes first,
 *                             because after a reload only the cookie is left)
 *   sign out → POST /logout  the browser sends the cookie, the server revokes it
 *                             and clears it
 */
export function createHttpAuthApi({
  api,
  publicApi,
}: {
  /** Authenticated: goes through `authFetch`. */
  api: ApiClient;
  /** Unauthenticated: sign-in and sign-out must never trigger a refresh. */
  publicApi: ApiClient;
}): AuthApi {
  async function currentUser(): Promise<SessionUser> {
    return toSessionUser(await unwrap(api.GET('/api/v1/auth/me')));
  }

  return {
    async signIn({ email, password }) {
      const pair = await unwrap(
        publicApi.POST('/api/v1/auth/login', { body: { email, password } }),
      );
      storeAccessToken(pair);
      return currentUser();
    },

    async restore() {
      // Whether there is a session to restore is the cookie's to say, and this
      // page cannot read it: ask. `authFetch` refreshes before the request.
      const result = await api.GET('/api/v1/auth/me');
      if (result.data) return toSessionUser(result.data);

      // No cookie, or one the API refused: signed out.
      if (result.response.status === 401) return null;

      // Anything else is an outage, not an answer — let the caller say so.
      throw new Error(`Could not restore the session (${result.response.status}).`);
    },

    async signOut() {
      // Cleared first: whatever the network does next, this tab is signed out.
      clearTokens();

      try {
        await publicApi.POST('/api/v1/auth/logout');
      } catch {
        // Offline. The access token is already gone from this tab, and the
        // cookie dies on its own in 30 days; the agent should not be stuck
        // signed in over it.
      }
    },

    async listAgencies() {
      const user = await currentUser();
      return user.agency ? [user.agency] : [];
    },

    async switchAgency() {
      // The API cannot yet issue a token scoped to a sub-agent (that arrives
      // with the sub-agent hierarchy in M3). `listAgencies` only ever offers the
      // current agency, so the switcher never calls this — it is here so the
      // port is complete and the gap is loud rather than silent.
      throw new Error('Switching agencies is not available yet.');
    },
  };
}

export function toSessionUser(me: Schemas['CurrentUserResponse']): SessionUser {
  return {
    userId: me.userId,
    email: me.email,
    roles: me.roles,
    agency: me.agencyId
      ? {
          id: me.agencyId,
          name: me.agencyName ?? 'Your agency',
          // `/me` does not say yet. Every agency is a principal until the
          // hierarchy lands (M3), so this is true today.
          kind: 'principal',
        }
      : null,
  };
}
