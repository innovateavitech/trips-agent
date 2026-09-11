import {
  needsRefresh,
  sessionFromTokens,
  type Session,
  type TokenPairResponse,
} from '../auth/session';
import type { SessionStore } from '../auth/session-store';
import { ApiError, NetworkError, readProblem } from './problem';

/**
 * The one place the console talks HTTP.
 *
 * Paths are relative (`/api/v1/...`). In development the Vite dev server forwards them to the API
 * — see vite.config.ts for why that is required rather than convenient.
 *
 * It owns the session's lifecycle so no screen has to think about it:
 *
 *   - attaches the access token to every request;
 *   - refreshes it shortly before it expires;
 *   - on a 401, refreshes once and retries once;
 *   - if refreshing fails, ends the session, and the router sends the reviewer to sign in.
 */
export interface ApiClient {
  get<T>(path: string): Promise<T>;
  post<T = void>(path: string, body?: unknown): Promise<T>;
  /**
   * Exchanges credentials for a session and returns it WITHOUT keeping it. The caller decides
   * whether the account belongs in this console before anything is stored — see AuthProvider.
   */
  signIn(email: string, password: string): Promise<Session>;
  /** Ends the session locally at once, then revokes the refresh token on the server. */
  signOut(): Promise<void>;
  /** Revokes one refresh token on the server. Best effort: never throws. */
  revoke(refreshToken: string): Promise<void>;
  /** After a page reload: trades the stored refresh token for a fresh session, if there is one. */
  restore(): Promise<Session | null>;
}

export interface ApiClientOptions {
  store: SessionStore;
  fetchImpl?: typeof fetch;
  now?: () => number;
}

export function createApiClient({
  store,
  // Wrapped rather than passed as `fetch`: calling a detached `window.fetch` throws
  // "Illegal invocation" in some browsers.
  fetchImpl = (input, init) => fetch(input, init),
  now = Date.now,
}: ApiClientOptions): ApiClient {
  /**
   * The refresh that is currently running, shared by everyone who needs one.
   *
   * This is the most important line in the file. Refresh tokens are single use, and the API
   * treats a second use of the same token as theft: it revokes the whole session (#16). The queue
   * page fires several requests at once; if each of them refreshed on its own, the second refresh
   * would present a token the first had already spent, and the reviewer would be signed out for
   * no visible reason. So there is only ever one refresh in flight, and everyone awaits it.
   */
  let refreshing: Promise<Session | null> | null = null;

  function refresh(): Promise<Session | null> {
    refreshing ??= exchangeRefreshToken().finally(() => {
      refreshing = null;
    });
    return refreshing;
  }

  async function exchangeRefreshToken(): Promise<Session | null> {
    const refreshToken = store.getRefreshToken();
    if (refreshToken === null) return null;

    const response = await send('POST', '/api/v1/auth/refresh', { refreshToken }, null);

    if (response.status === 401 || response.status === 400) {
      // Expired, revoked, or already used. There is no recovering this session.
      store.clear();
      return null;
    }

    if (!response.ok) {
      // The API is struggling, but the token may be perfectly good. Keep it and let the caller
      // show an error; the next attempt can try again.
      throw new ApiError(response.status, await readProblem(response));
    }

    const session = sessionFromTokens((await response.json()) as TokenPairResponse, now());
    store.set(session);
    return session;
  }

  async function currentAccessToken(): Promise<string | null> {
    const session = store.getSession();
    if (session && !needsRefresh(session, now())) return session.accessToken;
    if (store.getRefreshToken() === null) return null;

    const renewed = await refresh();
    return renewed?.accessToken ?? null;
  }

  async function send(
    method: string,
    path: string,
    body: unknown,
    accessToken: string | null,
  ): Promise<Response> {
    const headers: Record<string, string> = { Accept: 'application/json' };
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    if (accessToken) headers.Authorization = `Bearer ${accessToken}`;

    try {
      return await fetchImpl(path, {
        method,
        headers,
        body: body === undefined ? undefined : JSON.stringify(body),
      });
    } catch (error) {
      throw new NetworkError(error);
    }
  }

  async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
    const accessToken = await currentAccessToken();
    let response = await send(method, path, body, accessToken);

    if (response.status === 401 && accessToken !== null) {
      // Another request may already have refreshed while this one was in flight. If so, use its
      // result instead of spending the new refresh token on a second refresh.
      const current = store.getSession();
      const renewed =
        current && current.accessToken !== accessToken && !needsRefresh(current, now())
          ? current
          : await refresh();

      if (renewed === null) {
        throw new ApiError(401, { title: 'Your session has ended.', status: 401 });
      }

      response = await send(method, path, body, renewed.accessToken);
    }

    if (!response.ok) {
      throw new ApiError(response.status, await readProblem(response));
    }

    return readBody<T>(response);
  }

  async function revoke(refreshToken: string): Promise<void> {
    try {
      await send('POST', '/api/v1/auth/logout', { refreshToken }, null);
    } catch {
      // Unreachable server: the refresh token still expires on its own schedule.
    }
  }

  return {
    get: <T>(path: string) => request<T>('GET', path),
    post: <T = void>(path: string, body?: unknown) => request<T>('POST', path, body),

    async signIn(email, password) {
      const response = await send('POST', '/api/v1/auth/login', { email, password }, null);
      if (!response.ok) {
        throw new ApiError(response.status, await readProblem(response));
      }

      return sessionFromTokens((await response.json()) as TokenPairResponse, now());
    },

    async signOut() {
      const refreshToken = store.getRefreshToken();

      // Local first. Signing out has to work even when the API is unreachable — a reviewer
      // walking away from a shared machine must not be left signed in because a request failed.
      store.clear();

      if (refreshToken !== null) await revoke(refreshToken);
    },

    revoke,

    restore: () => {
      const session = store.getSession();
      return session ? Promise.resolve(session) : refresh();
    },
  };
}

async function readBody<T>(response: Response): Promise<T> {
  if (response.status === 204) return undefined as T;

  const contentType = response.headers.get('content-type') ?? '';
  if (!contentType.includes('json')) return undefined as T;

  return (await response.json()) as T;
}
