import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { agencyKeys, useAgenciesApi } from './agencies-api';
import type { AgencyAction, AgencyDirectoryQuery, UpdateAgencyRequest } from './types';

/** The directory changes when an agency signs up or is decided on. A minute is fresh enough. */
export const DIRECTORY_REFRESH_MS = 60_000;

export function useAgencyDirectory(query: AgencyDirectoryQuery) {
  const api = useAgenciesApi();

  return useQuery({
    queryKey: agencyKeys.list(query),
    queryFn: () => api.list(query),
    refetchInterval: DIRECTORY_REFRESH_MS,

    // Keeps the previous page on screen while the next one loads, so typing in the search box
    // does not blank the table between keystrokes.
    placeholderData: (previous) => previous,
  });
}

export function useAgencyProfile(agencyId: string) {
  const api = useAgenciesApi();

  return useQuery({
    queryKey: agencyKeys.profile(agencyId),
    queryFn: () => api.get(agencyId),
    enabled: agencyId !== '',
  });
}

/**
 * After any action — success, conflict or a lost connection — refetch everything.
 *
 * The server is the only trustworthy account of what happened. Patching the cache by hand would
 * show the admin the change they *meant* to make, which after a timeout may not be the one that
 * was made.
 */
function useInvalidateAgencies() {
  const queryClient = useQueryClient();
  return () => queryClient.invalidateQueries({ queryKey: agencyKeys.all });
}

/**
 * A lifecycle action. Never retried automatically: suspending twice is refused by the server, but
 * an automatic retry after a timeout could apply a decision the admin believes failed.
 */
export function useAgencyAction(agencyId: string) {
  const api = useAgenciesApi();
  const invalidate = useInvalidateAgencies();

  return useMutation({
    mutationFn: ({ action, reason }: { action: AgencyAction; reason: string }) =>
      api.act(agencyId, action, reason),
    onSettled: invalidate,
  });
}

export function useUpdateAgency(agencyId: string) {
  const api = useAgenciesApi();
  const invalidate = useInvalidateAgencies();

  return useMutation({
    mutationFn: (request: UpdateAgencyRequest) => api.update(agencyId, request),
    onSettled: invalidate,
  });
}

/** Fetches the export and hands it back. The screen decides how to save it. */
export function useAgencyExport(agencyId: string) {
  const api = useAgenciesApi();

  return useMutation({ mutationFn: () => api.exportData(agencyId) });
}

export { useInvalidateAgencies as useRefreshAgencies };
