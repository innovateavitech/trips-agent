import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { linkRenewalDelay } from './document-display';
import { kybReviewKeys, useKybReviewApi } from './kyb-review-api';

/** New submissions arrive whenever an agency sends its documents; a minute is fresh enough. */
export const QUEUE_REFRESH_MS = 60_000;

export function useKybQueue() {
  const api = useKybReviewApi();

  return useQuery({
    queryKey: kybReviewKeys.queue(),
    queryFn: () => api.getQueue(),
    refetchInterval: QUEUE_REFRESH_MS,
  });
}

/**
 * One submission, kept fresh enough that its document links always work.
 *
 * Each link dies ten minutes after the detail is fetched. Rather than let a reviewer who is
 * reading slowly click a dead link, the detail is refetched a minute before the earliest link
 * expires, and every link on the page is renewed with it.
 */
export function useKybSubmission(submissionId: string) {
  const api = useKybReviewApi();

  return useQuery({
    queryKey: kybReviewKeys.submission(submissionId),
    queryFn: () => api.getSubmission(submissionId),
    enabled: submissionId !== '',
    refetchInterval: (query) => linkRenewalDelay(query.state.data?.documents ?? [], Date.now()),
  });
}

/**
 * After any decision attempt — success, conflict or a lost connection — refetch everything.
 *
 * The server is the only trustworthy account of what happened. Patching the cache by hand would
 * show the reviewer the decision they *meant* to make, which after a timeout may not be the one
 * that was made.
 */
function useInvalidateKybReview() {
  const queryClient = useQueryClient();
  return () => queryClient.invalidateQueries({ queryKey: kybReviewKeys.all });
}

/**
 * Decisions are never retried automatically (mutations default to no retries, and main.tsx keeps
 * it that way). A retried approval is harmless, but a retried rejection after a timeout could
 * land after a colleague's approval, and the reviewer would never know it had been sent.
 */
export function useApproveSubmission(submissionId: string) {
  const api = useKybReviewApi();
  const invalidate = useInvalidateKybReview();

  return useMutation({
    mutationFn: () => api.approve(submissionId),
    onSettled: invalidate,
  });
}

export function useRejectSubmission(submissionId: string) {
  const api = useKybReviewApi();
  const invalidate = useInvalidateKybReview();

  return useMutation({
    mutationFn: (reason: string) => api.reject(submissionId, reason),
    onSettled: invalidate,
  });
}

export { useInvalidateKybReview as useRefreshKybReview };
