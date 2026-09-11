import { describe, expect, it, vi } from 'vitest';
import { staffToken } from '../../../test/fake-jwt';
import { createSessionStore, type TokenStorage } from '../../auth/session-store';
import { sessionFromTokens } from '../../auth/session';
import { createApiClient } from '../client';
import { ApiError, NetworkError } from '../problem';

/**
 * These tests are about the session, not about HTTP. What they pin down is the behaviour that is
 * invisible until it goes wrong: exactly one refresh at a time (a second use of a refresh token
 * is treated as theft and kills the session), one retry after a 401, and a sign-out that works
 * with the server unplugged.
 */

const QUEUE = '/api/v1/admin/kyb/submissions';
const REFRESH = '/api/v1/auth/refresh';
const LOGIN = '/api/v1/auth/login';
const LOGOUT = '/api/v1/auth/logout';

function memoryStorage(initial: Record<string, string> = {}): TokenStorage {
  const data: Record<string, string> = { ...initial };
  return {
    getItem: (key) => data[key] ?? null,
    setItem: (key, value) => {
      data[key] = value;
    },
    removeItem: (key) => {
      delete data[key];
    },
  };
}

let issued = 0;
function tokenPair(expiresInSeconds = 900) {
  issued += 1;
  return {
    accessToken: staffToken({ jti: `token-${issued}` }),
    refreshToken: `refresh-${issued}`,
    expiresInSeconds,
  };
}

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

const problem = (status: number, title: string) =>
  new Response(JSON.stringify({ title, status }), {
    status,
    headers: { 'Content-Type': 'application/problem+json' },
  });

interface Call {
  path: string;
  method: string;
  authorization: string | null;
  body: unknown;
}

/** A stand-in API that answers by `METHOD path` and records what it was asked. */
function fakeApi(handlers: Record<string, (call: Call) => Response | Promise<Response>>) {
  const calls: Call[] = [];

  const fetchImpl = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const headers = (init?.headers ?? {}) as Record<string, string>;
    const call: Call = {
      path: String(input),
      method: init?.method ?? 'GET',
      authorization: headers.Authorization ?? null,
      body: init?.body ? JSON.parse(String(init.body)) : undefined,
    };
    calls.push(call);

    const handler = handlers[`${call.method} ${call.path}`];
    if (!handler) throw new Error(`Unexpected request: ${call.method} ${call.path}`);
    return handler(call);
  });

  const countOf = (key: string) =>
    calls.filter((call) => `${call.method} ${call.path}` === key).length;

  return { fetchImpl: fetchImpl as unknown as typeof fetch, calls, countOf };
}

const storeWithSession = (pair: ReturnType<typeof tokenPair>, storage = memoryStorage()) => {
  const store = createSessionStore(storage);
  store.set(sessionFromTokens(pair, 0));
  return store;
};

describe('signing in', () => {
  it('does not keep the session — the caller decides whether the account belongs here', async () => {
    const pair = tokenPair();
    const { fetchImpl } = fakeApi({ [`POST ${LOGIN}`]: () => json(200, pair) });
    const store = createSessionStore(memoryStorage());
    const client = createApiClient({ store, fetchImpl, now: () => 0 });

    const session = await client.signIn('ops@tripsagent.test', 'password');

    expect(session.claims.email).toBe('ops@tripsagent.test');
    expect(store.getSession()).toBeNull();
    expect(store.getRefreshToken()).toBeNull();
  });

  it('reports a refusal as an ApiError carrying the server’s wording', async () => {
    const { fetchImpl } = fakeApi({
      [`POST ${LOGIN}`]: () => problem(401, 'That email address and password do not match.'),
    });
    const client = createApiClient({ store: createSessionStore(memoryStorage()), fetchImpl });

    await expect(client.signIn('ops@tripsagent.test', 'wrong')).rejects.toMatchObject({
      status: 401,
      message: 'That email address and password do not match.',
    });
  });
});

describe('requests', () => {
  it('sends the access token', async () => {
    const pair = tokenPair();
    const { fetchImpl, calls } = fakeApi({ [`GET ${QUEUE}`]: () => json(200, []) });
    const client = createApiClient({ store: storeWithSession(pair), fetchImpl, now: () => 0 });

    await client.get(QUEUE);

    expect(calls[0]?.authorization).toBe(`Bearer ${pair.accessToken}`);
  });

  it('turns a failed request into an ApiError with the problem details', async () => {
    const { fetchImpl } = fakeApi({
      [`GET ${QUEUE}`]: () => problem(403, 'You do not have access.'),
    });
    const client = createApiClient({
      store: storeWithSession(tokenPair()),
      fetchImpl,
      now: () => 0,
    });

    await expect(client.get(QUEUE)).rejects.toBeInstanceOf(ApiError);
  });

  it('reports an unreachable server as a NetworkError, not as a failed request', async () => {
    const fetchImpl = vi.fn(() =>
      Promise.reject(new TypeError('Failed to fetch')),
    ) as unknown as typeof fetch;
    const client = createApiClient({
      store: storeWithSession(tokenPair()),
      fetchImpl,
      now: () => 0,
    });

    await expect(client.get(QUEUE)).rejects.toBeInstanceOf(NetworkError);
  });
});

describe('refreshing', () => {
  it('refreshes once after a 401 and retries the request with the new token', async () => {
    const first = tokenPair();
    const second = tokenPair();
    const { fetchImpl, calls, countOf } = fakeApi({
      [`GET ${QUEUE}`]: (call) =>
        call.authorization === `Bearer ${first.accessToken}`
          ? problem(401, 'Unauthorized')
          : json(200, [{ submissionId: 's1' }]),
      [`POST ${REFRESH}`]: () => json(200, second),
    });
    const store = storeWithSession(first);
    const client = createApiClient({ store, fetchImpl, now: () => 0 });

    await expect(client.get(QUEUE)).resolves.toEqual([{ submissionId: 's1' }]);

    expect(countOf(`POST ${REFRESH}`)).toBe(1);
    expect(calls.at(0)?.body).toBeUndefined();
    expect(calls.at(1)?.body).toEqual({ refreshToken: first.refreshToken });
    expect(calls.at(2)?.authorization).toBe(`Bearer ${second.accessToken}`);
    expect(store.getSession()?.accessToken).toBe(second.accessToken);
  });

  it('shares ONE refresh between requests that fail at the same moment', async () => {
    // The heart of it. Refresh tokens are single use, and the API reads a second use as theft and
    // revokes the session — so three requests failing together must not refresh three times.
    const first = tokenPair();
    const second = tokenPair();
    const { fetchImpl, countOf } = fakeApi({
      [`GET ${QUEUE}`]: (call) =>
        call.authorization === `Bearer ${first.accessToken}`
          ? problem(401, 'Unauthorized')
          : json(200, []),
      [`POST ${REFRESH}`]: async () => {
        await new Promise((resolve) => setTimeout(resolve, 10));
        return json(200, second);
      },
    });
    const client = createApiClient({ store: storeWithSession(first), fetchImpl, now: () => 0 });

    await Promise.all([client.get(QUEUE), client.get(QUEUE), client.get(QUEUE)]);

    expect(countOf(`POST ${REFRESH}`)).toBe(1);
  });

  it('refreshes before sending a token that is about to expire', async () => {
    const nearlyDead = tokenPair(20); // inside the 30-second lead
    const fresh = tokenPair();
    const { fetchImpl, calls } = fakeApi({
      [`GET ${QUEUE}`]: () => json(200, []),
      [`POST ${REFRESH}`]: () => json(200, fresh),
    });
    const client = createApiClient({
      store: storeWithSession(nearlyDead),
      fetchImpl,
      now: () => 0,
    });

    await client.get(QUEUE);

    expect(calls[0]?.path).toBe(REFRESH);
    expect(calls[1]?.authorization).toBe(`Bearer ${fresh.accessToken}`);
  });

  it('restores a session from the stored refresh token after a page reload', async () => {
    const stored = tokenPair();
    const { fetchImpl, calls } = fakeApi({ [`POST ${REFRESH}`]: () => json(200, stored) });
    // A fresh store, as it is on boot: no session in memory, a refresh token on disk.
    const store = createSessionStore(
      memoryStorage({ 'trips.admin.refreshToken': 'from-last-boot' }),
    );
    const client = createApiClient({ store, fetchImpl, now: () => 0 });

    const session = await client.restore();

    expect(calls[0]?.body).toEqual({ refreshToken: 'from-last-boot' });
    expect(session?.accessToken).toBe(stored.accessToken);
    expect(store.getSession()).not.toBeNull();
  });

  it('ends the session when the refresh token is refused', async () => {
    const { fetchImpl } = fakeApi({
      [`GET ${QUEUE}`]: () => problem(401, 'Unauthorized'),
      [`POST ${REFRESH}`]: () => problem(401, 'That session has expired.'),
    });
    const store = storeWithSession(tokenPair());
    const client = createApiClient({ store, fetchImpl, now: () => 0 });

    await expect(client.get(QUEUE)).rejects.toMatchObject({ status: 401 });

    expect(store.getSession()).toBeNull();
    expect(store.getRefreshToken()).toBeNull();
  });

  it('keeps the session when refreshing fails for a reason other than the token', async () => {
    // A 503 says the API is unwell, not that the token is bad. Throwing the session away here
    // would sign everyone out every time the API restarted.
    const { fetchImpl } = fakeApi({
      [`GET ${QUEUE}`]: () => problem(401, 'Unauthorized'),
      [`POST ${REFRESH}`]: () => problem(503, 'Service unavailable'),
    });
    const store = storeWithSession(tokenPair());
    const client = createApiClient({ store, fetchImpl, now: () => 0 });

    await expect(client.get(QUEUE)).rejects.toMatchObject({ status: 503 });

    expect(store.getRefreshToken()).not.toBeNull();
  });
});

describe('signing out', () => {
  it('revokes the refresh token and clears the session', async () => {
    const pair = tokenPair();
    const { fetchImpl, calls } = fakeApi({
      [`POST ${LOGOUT}`]: () => new Response(null, { status: 204 }),
    });
    const store = storeWithSession(pair);
    const client = createApiClient({ store, fetchImpl, now: () => 0 });

    await client.signOut();

    expect(calls[0]?.body).toEqual({ refreshToken: pair.refreshToken });
    expect(store.getSession()).toBeNull();
  });

  it('signs out locally even when the server cannot be reached', async () => {
    const fetchImpl = vi.fn(() =>
      Promise.reject(new TypeError('Failed to fetch')),
    ) as unknown as typeof fetch;
    const store = storeWithSession(tokenPair());
    const client = createApiClient({ store, fetchImpl, now: () => 0 });

    await expect(client.signOut()).resolves.toBeUndefined();

    expect(store.getSession()).toBeNull();
    expect(store.getRefreshToken()).toBeNull();
  });
});
