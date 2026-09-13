import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { erasureKeys, useErasureApi } from './erasure-api';
import type { ErasurePreview, ErasureResult } from './types';

/** The erasures already carried out. No personal detail in any of them, by design. */
export function useErasureRequests() {
  const api = useErasureApi();

  return useQuery({
    queryKey: erasureKeys.list(),
    queryFn: () => api.list(),
  });
}

/**
 * Looking somebody up. A mutation rather than a query: it is a deliberate act with a body, and it
 * must not be repeated on its own when the window regains focus.
 */
export function usePreviewErasure() {
  const api = useErasureApi();

  return useMutation<ErasurePreview | null, Error, { agencyId: string; email: string }>({
    mutationFn: ({ agencyId, email }) => api.preview(agencyId, email),
  });
}

/** Carrying it out. Invalidates the list so the new record appears at the top. */
export function useEraseCustomer() {
  const api = useErasureApi();
  const queryClient = useQueryClient();

  return useMutation<
    ErasureResult,
    Error,
    { agencyId: string; customerId: string; reason: string }
  >({
    mutationFn: ({ agencyId, customerId, reason }) => api.erase(agencyId, customerId, reason),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: erasureKeys.all }),
  });
}
