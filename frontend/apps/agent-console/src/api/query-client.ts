import { QueryClient } from '@tanstack/react-query';
import { ApiError } from './http';

/**
 * TanStack Query holds ALL server state. There is deliberately no Redux/Zustand
 * store here — issue #48 says "no global store unless proven necessary", and so
 * far the only genuinely global client state is the session, which lives in
 * `AuthProvider`. Adding a store before there is state to put in it means every
 * later developer copies the pattern for things that are really just cache.
 */
export function createQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: {
        /**
         * Retrying a 4xx is pointless — a 403 stays a 403 — and on a 401 the
         * http() wrapper has already refreshed and replayed, so a retry here
         * would only duplicate work.
         */
        retry: (failureCount, error) => {
          if (error instanceof ApiError && error.status >= 400 && error.status < 500) {
            return false;
          }
          return failureCount < 2;
        },
        staleTime: 30_000,
        refetchOnWindowFocus: false,
      },
      mutations: {
        // Never automatically. A retried POST on this product can mean a second
        // wallet debit — see CLAUDE.md rule 6 for the same reasoning server-side.
        retry: false,
      },
    },
  });
}
