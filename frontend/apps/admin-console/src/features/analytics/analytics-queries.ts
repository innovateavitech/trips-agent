import { useQuery } from '@tanstack/react-query';
import { platformAnalyticsKeys, usePlatformAnalyticsApi } from './analytics-api';
import type { AnalyticsWindow } from './types';

/**
 * How often the browser asks again.
 *
 * The read models are rebuilt every five minutes and the server caches its answer for five, so
 * asking every two minutes picks a new figure up within a couple of minutes of it existing while
 * most requests cost nothing. The same rhythm the operations dashboard uses.
 */
export const REFRESH_MS = 120_000;

export function usePlatformAnalytics(window: AnalyticsWindow) {
  const api = usePlatformAnalyticsApi();

  return useQuery({
    queryKey: platformAnalyticsKeys.summary(window),
    queryFn: () => api.getSummary(window),
    refetchInterval: REFRESH_MS,
    refetchOnWindowFocus: true,

    // Changing the window keeps the old chart on screen while the new one loads.
    placeholderData: (previous) => previous,
  });
}

export function useSupplierPerformance(window: AnalyticsWindow) {
  const api = usePlatformAnalyticsApi();

  return useQuery({
    queryKey: platformAnalyticsKeys.suppliers(window),
    queryFn: () => api.getSuppliers(window),
    refetchInterval: REFRESH_MS,
    placeholderData: (previous) => previous,
  });
}

export function useReportExports() {
  const api = usePlatformAnalyticsApi();

  return useQuery({
    queryKey: platformAnalyticsKeys.exports(),
    queryFn: () => api.listExports(),

    // An export log does not need polling: somebody reading it is investigating, and they can
    // reload. Refetching on focus is enough to keep it honest.
    refetchOnWindowFocus: true,
  });
}
