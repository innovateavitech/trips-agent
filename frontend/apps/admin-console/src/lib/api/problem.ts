/**
 * How the API reports failure, and how the console tells the two kinds apart.
 *
 * Every error from TripsAgent.Api is an RFC 9457 "problem" — a small JSON body with a `title`,
 * an optional `detail`, and for validation failures an `errors` map. The titles are written for
 * people ("That email address and password do not match."), so the console shows them as they
 * are rather than inventing its own wording for the same thing.
 */
export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  /** Present on a validation problem: field name → messages. */
  errors?: Record<string, string[]>;
}

/** The server answered, and the answer was not a success. */
export class ApiError extends Error {
  readonly status: number;
  readonly problem: ProblemDetails | null;

  constructor(status: number, problem: ProblemDetails | null) {
    super(problem?.title ?? `Request failed with status ${status}`);
    this.name = 'ApiError';
    this.status = status;
    this.problem = problem;
  }
}

/**
 * The request never got an answer: the API is down, or the connection dropped.
 *
 * Kept separate from `ApiError` because the two mean different things for a decision. A 409 says
 * "nothing happened"; no answer at all says "we do not know whether it happened" — and the screen
 * has to tell the reviewer to check before trying again.
 */
export class NetworkError extends Error {
  constructor(cause: unknown) {
    super('The request did not reach the server.', { cause });
    this.name = 'NetworkError';
  }
}

/** Reads a problem body, or `null` when the response carries none (e.g. a bare 403). */
export async function readProblem(response: Response): Promise<ProblemDetails | null> {
  const contentType = response.headers.get('content-type') ?? '';
  if (!contentType.includes('json')) return null;

  try {
    const body: unknown = await response.json();
    return typeof body === 'object' && body !== null ? (body as ProblemDetails) : null;
  } catch {
    return null;
  }
}

/**
 * The first message for one field of a validation problem.
 *
 * Matched case-insensitively: the API names fields in camelCase today, but a Pascal-cased key
 * from a C# property name would otherwise be silently missed and the error never shown.
 */
export function fieldError(error: unknown, field: string): string | undefined {
  if (!(error instanceof ApiError)) return undefined;
  const errors = error.problem?.errors;
  if (!errors) return undefined;

  const key = Object.keys(errors).find((name) => name.toLowerCase() === field.toLowerCase());
  return key ? errors[key]?.[0] : undefined;
}

export interface ErrorCopy {
  title: string;
  detail: string;
}

/**
 * Plain-English copy for a failed read. Decisions have their own, stricter wording — see
 * features/kyb-review/decision-rules.ts.
 */
export function describeLoadError(error: unknown): ErrorCopy {
  if (error instanceof NetworkError) {
    return {
      title: 'Could not reach the server',
      detail: 'Check your connection, then try again.',
    };
  }

  if (error instanceof ApiError) {
    if (error.status === 401) {
      return { title: 'Your session has ended', detail: 'Sign in again to continue.' };
    }
    if (error.status === 403) {
      return {
        title: 'Your account cannot open this',
        detail: 'It is missing the permission this page needs. Ask a Super Admin to grant it.',
      };
    }
    if (error.status >= 500) {
      return {
        title: 'The server ran into a problem',
        detail: 'Try again in a moment. If it keeps happening, the API may be down.',
      };
    }
    return {
      title: error.problem?.title ?? 'The request failed',
      detail: error.problem?.detail ?? 'Try again in a moment.',
    };
  }

  return { title: 'Something went wrong', detail: 'Try again in a moment.' };
}
