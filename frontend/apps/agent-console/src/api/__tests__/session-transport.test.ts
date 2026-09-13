import { afterEach, describe, expect, it, vi } from 'vitest';
import { sessionFetch } from '../session-fetch';
import { createSessionTransport } from '../session-transport';
import { clearTokens, getAccessToken, setSessionExpiredHandler, storeAccessToken } from '../tokens';

/**
 * The session with the refresh token in an HttpOnly cookie (issue 107): nothing in the page holds
 * it, so a refresh carries no body and the browser must be told to send the cookie.
 */

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

afterEach(() => {
  clearTokens();
  setSessionExpiredHandler(null);
  vi.unstubAllGlobals();
});

describe('sessionFetch', () => {
  it('sends the cookie and names this console', async () => {
    const seen: Request[] = [];
    vi.stubGlobal(
      'fetch',
      vi.fn((request: Request) => {
        seen.push(request);
        return Promise.resolve(new Response(null, { status: 204 }));
      }),
    );

    await sessionFetch(new Request('http://localhost/api/v1/auth/logout', { method: 'POST' }));

    expect(seen[0]?.credentials).toBe('include');
    expect(seen[0]?.headers.get('X-Session-Client')).toBe('agent');
  });
});

describe('session transport', () => {
  it('refreshes from the cookie, with no body, before the first request after a reload', async () => {
    const requests: Request[] = [];
    const transport = createSessionTransport({
      baseUrl: 'http://localhost',
      fetch: async (request) => {
        requests.push(request);
        if (request.url.endsWith('/api/v1/auth/refresh')) {
          return json(200, { accessToken: 'fresh', expiresInSeconds: 900 });
        }
        return json(200, { ok: true });
      },
    });

    const response = await transport.authFetch(new Request('http://localhost/api/v1/auth/me'));

    expect(response.status).toBe(200);
    expect(requests[0]?.url).toMatch(/\/api\/v1\/auth\/refresh$/);
    expect(await requests[0]?.text()).toBe('');
    expect(requests[1]?.headers.get('Authorization')).toBe('Bearer fresh');
    expect(getAccessToken()).toBe('fresh');
  });

  it('treats a refused refresh with no session as signed out, asking only once', async () => {
    const expired = vi.fn();
    setSessionExpiredHandler(expired);
    let refreshes = 0;

    const transport = createSessionTransport({
      baseUrl: 'http://localhost',
      fetch: async (request) => {
        if (request.url.endsWith('/api/v1/auth/refresh')) refreshes += 1;
        return json(401, { title: 'That session has expired.' });
      },
    });

    const response = await transport.authFetch(new Request('http://localhost/api/v1/auth/me'));

    expect(response.status).toBe(401);
    expect(refreshes).toBe(1);
    expect(expired).not.toHaveBeenCalled();
  });

  it('ends the session when a signed-in request is refused and the cookie is too', async () => {
    const expired = vi.fn();
    setSessionExpiredHandler(expired);
    storeAccessToken({ accessToken: 'stale' });

    const transport = createSessionTransport({
      baseUrl: 'http://localhost',
      fetch: async () => json(401, { title: 'Unauthorized' }),
    });

    await transport.authFetch(new Request('http://localhost/api/v1/bookings'));

    expect(expired).toHaveBeenCalledOnce();
    expect(getAccessToken()).toBeNull();
  });
});
