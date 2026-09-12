import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { billingKeys, useBillingApi } from './billing-api';
import type {
  MigrateSubscribersRequest,
  SaveTierRequest,
  SetTierEntitlementsRequest,
  SetTierPriceRequest,
  TierReasonRequest,
} from './types';

/** Fixed in the backend's code, so it changes on a deploy and never while somebody is on the screen. */
export function useEntitlementCatalogue() {
  const api = useBillingApi();

  return useQuery({
    queryKey: billingKeys.catalogue(),
    queryFn: () => api.catalogue(),
    staleTime: 60 * 60 * 1000,
  });
}

export function useTiers(includeArchived: boolean) {
  const api = useBillingApi();

  return useQuery({
    queryKey: billingKeys.tiers(includeArchived),
    queryFn: () => api.tiers(includeArchived),
  });
}

export function useSubscribers(tierId?: string) {
  const api = useBillingApi();

  return useQuery({
    queryKey: billingKeys.subscribers(tierId),
    queryFn: () => api.subscribers(tierId),
  });
}

/**
 * After any change — success, conflict or a lost connection — refetch.
 *
 * The server is the only trustworthy account of what happened. Patching the cache by hand would
 * show the change somebody *meant* to make, and after a timeout that may not be the one that was
 * made. It matters more here than most places: the subscriber count is what decides whether a tier
 * can be deleted at all.
 */
function useInvalidate() {
  const queryClient = useQueryClient();
  return () => queryClient.invalidateQueries({ queryKey: billingKeys.all });
}

export function useCreateTier() {
  const api = useBillingApi();
  const invalidate = useInvalidate();

  return useMutation({
    mutationFn: (request: SaveTierRequest) => api.create(request),
    onSettled: invalidate,
  });
}

export function useUpdateTier(tierId: string) {
  const api = useBillingApi();
  const invalidate = useInvalidate();

  return useMutation({
    mutationFn: (request: SaveTierRequest) => api.update(tierId, request),
    onSettled: invalidate,
  });
}

export function useSetTierPrice(tierId: string) {
  const api = useBillingApi();
  const invalidate = useInvalidate();

  return useMutation({
    mutationFn: (request: SetTierPriceRequest) => api.setPrice(tierId, request),
    onSettled: invalidate,
  });
}

export function useSetTierEntitlements(tierId: string) {
  const api = useBillingApi();
  const invalidate = useInvalidate();

  return useMutation({
    mutationFn: (request: SetTierEntitlementsRequest) => api.setEntitlements(tierId, request),
    onSettled: invalidate,
  });
}

/**
 * Publish, archive, restore and delete, which differ only in the verb.
 *
 * Never retried automatically, like every other write in this console: a retried archive after a
 * timeout can retire a plan somebody believes is still on sale.
 */
export function useTierLifecycle(tierId: string) {
  const api = useBillingApi();
  const invalidate = useInvalidate();

  return useMutation({
    mutationFn: ({
      action,
      reason,
    }: {
      action: 'publish' | 'archive' | 'restore' | 'delete';
      reason: string;
    }) => {
      const request: TierReasonRequest = { reason };

      if (action === 'publish') return api.publish(tierId, request);
      if (action === 'archive') return api.archive(tierId, request);
      if (action === 'restore') return api.restore(tierId, request);

      return api.remove(tierId, reason);
    },
    onSettled: invalidate,
  });
}

export function useMigrateSubscribers(tierId: string) {
  const api = useBillingApi();
  const invalidate = useInvalidate();

  return useMutation({
    mutationFn: (request: MigrateSubscribersRequest) => api.migrate(tierId, request),
    onSettled: invalidate,
  });
}
