import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { analyticsKeys, useAnalyticsApi } from './analytics-api';
import type { AnalyticsWindow } from './types';

/** A drill-down page. Fifty rows: enough to scan, small enough to render instantly. */
export const BOOKINGS_PAGE_SIZE = 50;

/**
 * How long the dashboard's answer is believed.
 *
 * The read models are rebuilt every five minutes and the server caches nothing
 * for an agency, so anything shorter than that only adds requests without
 * adding information.
 */
const SUMMARY_STALE_MS = 5 * 60_000;

export function useAgencyAnalytics(window: AnalyticsWindow) {
  const api = useAnalyticsApi();

  return useQuery({
    queryKey: analyticsKeys.summary(window),
    queryFn: () => api.getSummary(window),
    staleTime: SUMMARY_STALE_MS,

    // Changing the window should not blank the page; the old chart stays while
    // the new one loads.
    placeholderData: (previous) => previous,
  });
}

export function useBookingDrillDown(window: AnalyticsWindow, page: number, enabled = true) {
  const api = useAnalyticsApi();

  return useQuery({
    queryKey: analyticsKeys.bookings(window, page),
    queryFn: () => api.getBookings(window, page, BOOKINGS_PAGE_SIZE),
    enabled,
    placeholderData: (previous) => previous,
  });
}

export function useReportDefinitions() {
  const api = useAnalyticsApi();

  return useQuery({
    queryKey: analyticsKeys.definitions(),
    queryFn: () => api.listDefinitions(),

    // The catalogue changes with a deployment, not with a booking.
    staleTime: 60 * 60_000,
  });
}

/**
 * Recent report runs.
 *
 * Polled while anything is still queued or running, and left alone once
 * everything has finished — a report that is being produced in the background
 * is precisely the case where the agent is watching this list.
 */
export function useReportJobs() {
  const api = useAnalyticsApi();

  return useQuery({
    queryKey: analyticsKeys.jobs(),
    queryFn: () => api.listJobs(),
    refetchInterval: (query) =>
      query.state.data?.some((job) => job.status === 'Queued' || job.status === 'Running')
        ? 5_000
        : false,
  });
}

export function useRunReport() {
  const api = useAnalyticsApi();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: ({ definitionCode, window }: { definitionCode: string; window: AnalyticsWindow }) =>
      api.runReport(definitionCode, window),

    // A run is recorded whether it answered with a file or was queued, so the
    // list is stale either way.
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: analyticsKeys.jobs() }),
  });
}

export function useDownloadReport() {
  const api = useAnalyticsApi();

  return useMutation({
    mutationFn: (jobId: string) => api.downloadJob(jobId),
  });
}
