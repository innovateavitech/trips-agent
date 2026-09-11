import { createApiClient } from '@trips/api-client';
import { API_BASE_URL } from './config';
import { createSessionTransport } from './session-transport';

/**
 * The authenticated, typed client every screen uses.
 *
 * ```ts
 * import { api } from '@/api/client';   // or a relative import
 * import { unwrap } from '@/api/errors';
 *
 * useQuery({
 *   queryKey: ['kyb', 'status'],
 *   queryFn: () => unwrap(api.GET('/api/v1/kyb/status')),
 * });
 * ```
 *
 * Paths and bodies are checked against `@trips/api-client`, which is generated
 * from the API's OpenAPI document. Authentication and token refresh are handled
 * inside `authFetch` — a screen never adds a header or handles a 401 itself.
 */
export const sessionTransport = createSessionTransport({ baseUrl: API_BASE_URL });

export const api = createApiClient({ baseUrl: API_BASE_URL, fetch: sessionTransport.authFetch });
