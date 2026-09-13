import { useQuery } from '@tanstack/react-query';
import { auditKeys, useAuditApi } from './audit-api';
import type { AuditLogQuery } from './types';

export function useAuditLog(query: AuditLogQuery) {
  const api = useAuditApi();

  return useQuery({
    queryKey: auditKeys.page(query),
    queryFn: () => api.search(query),

    // Keeps the current page on screen while the next one loads, so changing a filter does not
    // blank the table under somebody mid-read.
    placeholderData: (previous) => previous,
  });
}

/**
 * The action names to offer in the filter.
 *
 * Cached for an hour. The set of actions the platform can write changes when the code changes,
 * not while somebody is looking at the screen, and it is a distinct round trip on every page load
 * otherwise.
 */
export function useAuditActions() {
  const api = useAuditApi();

  return useQuery({
    queryKey: auditKeys.actions(),
    queryFn: () => api.actions(),
    staleTime: 60 * 60 * 1000,
  });
}
