import { QueryClient } from '@tanstack/react-query';
import { ApiError } from '../api/errors';

/** How many times a failed READ is retried before its error state shows. */
export const MAX_QUERY_RETRIES = 2;

/**
 * Whether a failed query is worth asking again.
 *
 * A 4xx is an answer, not an accident: a 403 stays a 403 however often you ask,
 * and retrying it only makes the agent wait three times as long to be told. A
 * 5xx or a dropped connection might well succeed a second later.
 *
 * A 401 never reaches here as something to retry — `authFetch` has already
 * refreshed and retried it once, so a 401 at this point means the session is
 * genuinely over.
 */
export function shouldRetryQuery(failureCount: number, error: unknown): boolean {
  if (error instanceof ApiError && error.status < 500) return false;
  return failureCount < MAX_QUERY_RETRIES;
}

/**
 * TanStack Query holds all server state. There is no Redux/Zustand store: the
 * only client state the shell keeps is who is signed in, and that is one React
 * context (`AuthProvider`). Add a global store only when something proves it
 * needs one.
 */
export function createQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: {
        retry: shouldRetryQuery,
        // Long enough that moving between screens does not refetch everything;
        // short enough that numbers do not go stale on screen.
        staleTime: 15_000,
      },
      mutations: {
        // NEVER retried automatically. A mutation here moves money or books a
        // seat; a timed-out one has an UNKNOWN outcome, and sending it again can
        // double-charge a wallet or issue a second ticket (ADR 0003). A mutation
        // that wants a retry must opt in knowing it is idempotent.
        retry: false,
      },
    },
  });
}
