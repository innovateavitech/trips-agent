import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError, http, resetRefreshState } from './http';
import { clearAccessToken, setAccessToken, setSessionExpiredHandler } from './tokens';

/**
 * These cover acceptance criterion 3 on issue #48 — "token refresh handled
 * transparently on 401" — and in particular the single-flight behaviour, which
 * is the part that is easy to get wrong and expensive when it is wrong: #16
 * revokes the whole refresh chain if one single-use token is presented twice.
 */

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function unauthorized(): Response {
  return new Response(JSON.stringify({ title: 'Unauthorized' }), {
    status: 401,
    headers: { 'Content-Type': 'application/json' },
  });
}

function authHeaderOf(call: unknown[]): string | null {
  const init = call[1] as RequestInit | undefined;
  return new Headers(init?.headers).get('Authorization');
}

let fetchMock: ReturnType<typeof vi.fn>;

beforeEach(() => {
  resetRefreshState();
  clearAccessToken();
  setAccessToken('expired-token');
  fetchMock = vi.fn();
  vi.stubGlobal('fetch', fetchMock);
});

afterEach(() => {
  setSessionExpiredHandler(null);
  resetRefreshState();
  clearAccessToken();
  vi.unstubAllGlobals();
});

describe('http', () => {
  it('sends the access token and returns the parsed body', async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse({ id: 'a1' }));

    await expect(http<{ id: string }>('/agencies/a1')).resolves.toEqual({ id: 'a1' });
    expect(authHeaderOf(fetchMock.mock.calls[0]!)).toBe('Bearer expired-token');
  });

  it('refreshes once on 401 and replays the request with the new token', async () => {
    fetchMock
      .mockResolvedValueOnce(unauthorized())
      .mockResolvedValueOnce(jsonResponse({ accessToken: 'fresh-token' }))
      .mockResolvedValueOnce(jsonResponse({ balanceMinor: 150000 }));

    await expect(http('/wallet')).resolves.toEqual({ balanceMinor: 150000 });

    expect(fetchMock).toHaveBeenCalledTimes(3);
    expect(fetchMock.mock.calls[1]![0]).toContain('/auth/refresh');
    // The replay must carry the NEW token, not the stale one.
    expect(authHeaderOf(fetchMock.mock.calls[2]!)).toBe('Bearer fresh-token');
  });

  it('refreshes only ONCE when several requests 401 together', async () => {
    // Three concurrent calls all hit an expired token.
    fetchMock.mockImplementation((url: string, init?: RequestInit) => {
      if (String(url).includes('/auth/refresh')) {
        return Promise.resolve(jsonResponse({ accessToken: 'fresh-token' }));
      }
      const token = new Headers(init?.headers).get('Authorization');
      return Promise.resolve(
        token === 'Bearer fresh-token' ? jsonResponse({ ok: true }) : unauthorized(),
      );
    });

    const results = await Promise.all([http('/a'), http('/b'), http('/c')]);

    expect(results).toEqual([{ ok: true }, { ok: true }, { ok: true }]);

    const refreshCalls = fetchMock.mock.calls.filter((c) => String(c[0]).includes('/auth/refresh'));
    // The whole point: one refresh, not three. Three would spend a single-use
    // token twice and trip #16's reuse detection.
    expect(refreshCalls).toHaveLength(1);
  });

  it('does not retry a second time when the replay also 401s', async () => {
    fetchMock
      .mockResolvedValueOnce(unauthorized())
      .mockResolvedValueOnce(jsonResponse({ accessToken: 'fresh-token' }))
      .mockResolvedValueOnce(unauthorized());

    await expect(http('/forbidden')).rejects.toBeInstanceOf(ApiError);
    // 1 original + 1 refresh + 1 replay. No loop.
    expect(fetchMock).toHaveBeenCalledTimes(3);
  });

  it('signals session expiry when the refresh itself is rejected', async () => {
    const onExpired = vi.fn();
    setSessionExpiredHandler(onExpired);

    fetchMock
      .mockResolvedValueOnce(unauthorized())
      .mockResolvedValueOnce(new Response(null, { status: 401 }));

    await expect(http('/wallet')).rejects.toMatchObject({ status: 401 });
    expect(onExpired).toHaveBeenCalledTimes(1);
  });

  it('never attaches a token when withAuth is false', async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse({ ok: true }));

    await http('/auth/login', { method: 'POST', body: { email: 'a@b.com' }, withAuth: false });

    expect(authHeaderOf(fetchMock.mock.calls[0]!)).toBeNull();
  });

  it('surfaces the ProblemDetails detail on a failure', async () => {
    fetchMock.mockResolvedValueOnce(
      jsonResponse({ title: 'Bad Request', detail: 'Amount is below the minimum' }, 400),
    );

    await expect(http('/wallet/topup')).rejects.toMatchObject({
      status: 400,
      detail: 'Amount is below the minimum',
    });
  });

  it('returns undefined for a 204', async () => {
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 204 }));
    await expect(http('/sessions/current')).resolves.toBeUndefined();
  });
});
