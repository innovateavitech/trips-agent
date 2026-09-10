import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { StatementFilters } from './types';
import { useWalletApi, walletKeys } from './wallet-api';

export const STATEMENT_PAGE_SIZE = 20;

export function useWalletSummary() {
  const api = useWalletApi();

  return useQuery({
    queryKey: walletKeys.summary(),
    queryFn: () => api.getSummary(),
    // A balance goes stale the moment a colleague books something. Half a
    // minute is short enough that the number on screen is believable and long
    // enough that a busy agency is not hammering the endpoint.
    staleTime: 30_000,
  });
}

export function useStatement(filters: StatementFilters, page: number) {
  const api = useWalletApi();

  return useQuery({
    queryKey: walletKeys.statement(filters, page),
    queryFn: () => api.getStatement(filters, page, STATEMENT_PAGE_SIZE),
    // Keeps the previous page rendered while the next one loads, so paging
    // does not blank the table and bounce the scroll position.
    placeholderData: (previous) => previous,
  });
}

export function useStartTopUp() {
  const api = useWalletApi();

  return useMutation({
    mutationFn: (amountMinor: number) => api.startTopUp(amountMinor),
  });
}

/**
 * Polls the server for the outcome of a top-up.
 *
 * Polling, not a webhook or a push: the agent lands back on our return page the
 * instant Paystack redirects them, which is often *before* Paystack's webhook
 * has reached our server. So we ask, and keep asking while the answer is
 * `pending`. This mirrors how the supplier integration learns booking outcomes
 * — see CLAUDE.md, "the Trips Africa API has no webhooks".
 *
 * Nothing here credits the wallet. It reports a result the server already
 * decided.
 */
export function useTopUpResult(reference: string | null) {
  const api = useWalletApi();

  return useQuery({
    queryKey: walletKeys.topUp(reference ?? 'none'),
    queryFn: () => api.getTopUp(reference as string),
    enabled: reference !== null,
    refetchInterval: (query) =>
      query.state.data?.status === 'pending' ? TOP_UP_POLL_INTERVAL_MS : false,
    // The tab may be backgrounded while the agent finishes on their phone.
    refetchIntervalInBackground: true,
  });
}

export const TOP_UP_POLL_INTERVAL_MS = 2_000;

/**
 * After a confirmed top-up, throw away every cached wallet answer.
 *
 * We refetch rather than patching the cached balance by hand. Adding the
 * top-up amount to the number we happen to be holding would be a *guess* — a
 * booking may have debited the wallet in the meantime, and a balance that is
 * confidently wrong is worse than one that is briefly loading.
 */
export function useRefreshWallet() {
  const queryClient = useQueryClient();
  return () => queryClient.invalidateQueries({ queryKey: walletKeys.all });
}
