/**
 * A non-2xx answer from the API, carrying the status so a caller can tell a 403
 * ("you may not") from a 404 ("not there") from a 500 ("our fault").
 *
 * `title` and `detail` come from the ASP.NET ProblemDetails body, which the API
 * writes in plain language for exactly this purpose — "That email address and
 * password do not match." — so screens show them rather than inventing copy.
 */
export class ApiError extends Error {
  readonly status: number;
  readonly title: string;
  readonly detail: string | undefined;

  constructor(status: number, title: string, detail?: string) {
    super(detail ? `${title} ${detail}` : title);
    this.name = 'ApiError';
    this.status = status;
    this.title = title;
    this.detail = detail;
  }

  /** Builds one from whatever the API returned — ProblemDetails, or nothing useful. */
  static from(response: Response, body: unknown): ApiError {
    const problem = (body && typeof body === 'object' ? body : {}) as {
      title?: unknown;
      detail?: unknown;
    };

    const title =
      typeof problem.title === 'string' && problem.title.length > 0
        ? problem.title
        : `The request failed (${response.status}).`;
    const detail = typeof problem.detail === 'string' ? problem.detail : undefined;

    return new ApiError(response.status, title, detail);
  }
}

/**
 * What the generated client returns. `data` is optional in its types even on a
 * 2xx, so taking it at face value makes every caller's type `T | undefined`.
 */
type ClientResult<T> = { data?: T; error?: unknown; response: Response };

/**
 * Turns the generated client's `{ data, error }` into a value or a thrown
 * `ApiError` — the shape TanStack Query wants from a `queryFn`, so a failed
 * request lands in `query.error` and renders the shared `<ErrorState>`.
 *
 * ```ts
 * queryFn: () => unwrap(api.GET('/api/v1/auth/me')),
 * ```
 */
export async function unwrap<T>(pending: Promise<ClientResult<T>>): Promise<NonNullable<T>> {
  const result = await pending;
  if (result.error !== undefined || !result.response.ok) {
    throw ApiError.from(result.response, result.error);
  }

  if (result.data === undefined || result.data === null) {
    // A 2xx with nothing in it, where the caller needs a value. That is the API
    // breaking its own contract, so say so rather than hand back undefined and
    // let it surface three screens later as "cannot read property of undefined".
    throw new ApiError(
      result.response.status,
      'The server returned no content.',
      'Please try again. If it keeps happening, contact support.',
    );
  }

  return result.data as NonNullable<T>;
}

/** What an error means to the agent: a headline and what to do about it. */
export interface ErrorDescription {
  title: string;
  detail: string;
}

/**
 * Turns anything a request can throw into words for the shared `<ErrorState>`.
 *
 *   - An `ApiError` below 500 carries the API's own plain-language message.
 *   - A 5xx is our fault, and says so, without leaking a stack trace.
 *   - A `TypeError` from `fetch` is the network: no answer came back at all.
 *     On Nigerian mobile data that is common, and it deserves its own advice.
 */
export function describeError(error: unknown): ErrorDescription {
  if (error instanceof ApiError) {
    if (error.status >= 500) {
      return {
        title: 'Something went wrong on our side',
        detail: 'It is not something you did. Please try again in a moment.',
      };
    }
    return {
      title: error.title,
      detail: error.detail ?? 'Please check the details and try again.',
    };
  }

  if (error instanceof TypeError) {
    return {
      title: 'We could not reach the server',
      detail: 'Check your internet connection, then try again.',
    };
  }

  return {
    title: 'Something went wrong',
    detail: 'Please try again. If it keeps happening, contact support.',
  };
}
