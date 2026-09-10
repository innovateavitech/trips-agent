import { apiUrl } from './config';
import { RefreshFailedError, refreshAccessToken } from './refresh';
import { getAccessToken, notifySessionExpired } from './tokens';

export interface HttpOptions extends Omit<RequestInit, 'body'> {
  /** Serialised to JSON automatically. Pass `FormData` through `rawBody` instead. */
  body?: unknown;
  rawBody?: BodyInit;
  /** Set false for the handful of endpoints that must not carry a token. */
  withAuth?: boolean;
}

/** A non-2xx response. Carries the status so callers can branch on 403 vs 404. */
export class ApiError extends Error {
  readonly status: number;
  readonly detail: string | undefined;
  readonly body: unknown;

  constructor(status: number, message: string, detail?: string, body?: unknown) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.detail = detail;
    this.body = body;
  }
}

/**
 * ---------------------------------------------------------------------------
 *  Single-flight refresh.
 * ---------------------------------------------------------------------------
 *  A dashboard fires several queries at once. When the access token expires
 *  they all 401 at the same moment. Refreshing per request would send five
 *  refreshes with the SAME single-use token — and #16 revokes the whole chain
 *  when a refresh token is presented twice, so the naive version logs the agent
 *  out precisely when it is trying to keep them signed in.
 *
 *  So: the first 401 starts a refresh and every later caller awaits that same
 *  promise. One token spent, one new pair issued.
 */
let inFlightRefresh: Promise<string> | null = null;

function refreshOnce(): Promise<string> {
  inFlightRefresh ??= refreshAccessToken().finally(() => {
    inFlightRefresh = null;
  });
  return inFlightRefresh;
}

/** Exposed for tests, and for logout, which must not leave a refresh mid-flight. */
export function resetRefreshState(): void {
  inFlightRefresh = null;
}

function buildInit(options: HttpOptions): RequestInit {
  const { body, rawBody, withAuth = true, headers, ...rest } = options;

  const finalHeaders = new Headers(headers);
  finalHeaders.set('Accept', 'application/json');

  const token = withAuth ? getAccessToken() : null;
  if (token) finalHeaders.set('Authorization', `Bearer ${token}`);

  let finalBody: BodyInit | undefined;
  if (rawBody !== undefined) {
    finalBody = rawBody;
  } else if (body !== undefined) {
    finalHeaders.set('Content-Type', 'application/json');
    finalBody = JSON.stringify(body);
  }

  return { ...rest, headers: finalHeaders, credentials: 'include', body: finalBody };
}

async function parseError(response: Response): Promise<ApiError> {
  let detail: string | undefined;
  let parsed: unknown;

  try {
    parsed = await response.clone().json();
    if (parsed && typeof parsed === 'object') {
      // ASP.NET ProblemDetails: { title, detail, status }
      const problem = parsed as { detail?: unknown; title?: unknown };
      if (typeof problem.detail === 'string') detail = problem.detail;
      else if (typeof problem.title === 'string') detail = problem.title;
    }
  } catch {
    // Not JSON — an nginx 502 or similar. The status alone is the story.
  }

  return new ApiError(
    response.status,
    detail ?? `Request failed with ${response.status}`,
    detail,
    parsed,
  );
}

/**
 * The single way this app talks to the API.
 *
 * On a 401 it refreshes the access token once and replays the request, so no
 * feature screen ever has to think about token expiry — that is the whole point
 * of acceptance criterion 3 on issue #48.
 *
 * A second 401 after a successful refresh is NOT retried again: the token is
 * demonstrably fine, so the real answer is "this user may not do this", and
 * looping would just hammer the endpoint.
 */
export async function http<T>(path: string, options: HttpOptions = {}): Promise<T> {
  const url = apiUrl(path);
  const withAuth = options.withAuth ?? true;

  let response = await fetch(url, buildInit(options));

  if (response.status === 401 && withAuth) {
    try {
      await refreshOnce();
    } catch (error) {
      if (error instanceof RefreshFailedError) {
        // The refresh credential itself is dead. Nothing to retry.
        notifySessionExpired();
        throw new ApiError(401, 'Your session has expired. Please sign in again.');
      }
      throw error;
    }

    // Rebuild the init so the replay picks up the NEW Authorization header.
    response = await fetch(url, buildInit(options));
  }

  if (!response.ok) {
    throw await parseError(response);
  }

  if (response.status === 204 || response.headers.get('Content-Length') === '0') {
    return undefined as T;
  }

  return (await response.json()) as T;
}
