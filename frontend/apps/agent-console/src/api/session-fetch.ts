/**
 * The transport under every call this console makes to the API.
 *
 * Two things, both about the refresh cookie (issue 107):
 *
 *   - `credentials: 'include'`, so the browser sends the cookie and accepts a
 *     new one even when the API is on another origin (`VITE_API_BASE_URL`).
 *     Through the dev proxy the page and the API share an origin and this
 *     changes nothing.
 *   - `X-Session-Client: agent`. Cookies belong to a host, not a port, so on
 *     `localhost` the agent and admin consoles share a cookie jar; the API gives
 *     each console a cookie of its own by this header.
 */

export const SESSION_CLIENT_HEADER = 'X-Session-Client';

export function sessionFetch(request: Request): Promise<Response> {
  const next = new Request(request, { credentials: 'include' });
  next.headers.set(SESSION_CLIENT_HEADER, 'agent');
  return globalThis.fetch(next);
}

/**
 * Runs `task` while holding a lock every tab of this console shares.
 *
 * The refresh token is single use, and the cookie is shared by every tab. Two
 * tabs refreshing at the same instant would present the same token twice, and
 * the API treats that as theft — it revokes the session everywhere. With the
 * lock the second tab waits, and by the time it refreshes the browser already
 * holds the rotated cookie. Browsers without the Web Locks API just run the task.
 */
export async function withRefreshLock<T>(task: () => Promise<T>): Promise<T> {
  const locks = typeof navigator !== 'undefined' ? navigator.locks : undefined;
  if (!locks) return task();
  return await locks.request('agent-console-session-refresh', task);
}
