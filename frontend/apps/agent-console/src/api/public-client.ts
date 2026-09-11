import { createApiClient } from '@trips/api-client';
import { API_BASE_URL } from './config';

/**
 * The client for the few calls that must NOT go through `authFetch`: sign in,
 * refresh and sign out.
 *
 * Each of those can legitimately answer 401 — wrong password, expired refresh
 * token — and `authFetch` treats a 401 as "refresh and retry". Sent through it,
 * a failed refresh would try to refresh itself, and a wrong password would
 * spend the agent's refresh token. Keeping them on a plain client makes that
 * impossible rather than merely avoided.
 */
export const publicApi = createApiClient({ baseUrl: API_BASE_URL });
