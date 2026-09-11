import createClient, { type Client } from 'openapi-fetch';
import type { components, paths } from './generated/schema';

/**
 * The typed client for the Trips Agent API.
 *
 * The types come from `src/generated/schema.ts`, which is generated from the
 * API's own OpenAPI document — so a request to a path that does not exist, or a
 * body missing a field the C# record requires, is a compile error rather than a
 * 400 in production.
 *
 * This file is the only hand-written part of the package and should stay tiny:
 * how a request is authenticated or retried is the calling app's business, and
 * it plugs that in through `fetch`.
 */

export type { components, paths };

/** Every DTO the API exposes, by its C# record name: `Schemas['CurrentUserResponse']`. */
export type Schemas = components['schemas'];

export type ApiClient = Client<paths>;

export interface ApiClientOptions {
  /**
   * Prefix for every path. The paths in the schema already start with
   * `/api/v1/…`, so this is the origin only — or `''` to call the page's own
   * origin through a dev-server proxy.
   */
  baseUrl: string;

  /**
   * The transport. Pass one that adds the bearer token and refreshes it on a
   * 401; leave it out for calls that must go unauthenticated, such as signing in.
   */
  fetch?: (request: Request) => Promise<Response>;
}

export function createApiClient({ baseUrl, fetch }: ApiClientOptions): ApiClient {
  return createClient<paths>({
    baseUrl,
    // Looked up on every call rather than captured now. Capturing
    // `globalThis.fetch` at creation would pin whichever fetch existed when the
    // module loaded, and a test that stubs fetch afterwards would silently hit
    // the network instead of its stub.
    fetch: fetch ?? ((request) => globalThis.fetch(request)),
  });
}
