import { useQuery } from '@tanstack/react-query';
import { dashboardKeys, useDashboardApi } from './dashboard-api';

/**
 * How often the browser asks again.
 *
 * The acceptance criterion is metrics no more than ten minutes stale, and the server caches its
 * answer for five. Asking every two minutes means the screen picks up a new count within a couple
 * of minutes of it being recounted, while three of every four requests are served from that cache
 * and cost nothing.
 */
export const DASHBOARD_REFRESH_MS = 120_000;

export function useOperationsDashboard() {
  const api = useDashboardApi();

  return useQuery({
    queryKey: dashboardKeys.all,
    queryFn: () => api.get(),
    refetchInterval: DASHBOARD_REFRESH_MS,

    // The tab is often left open on a wall screen. Refetching when it regains focus is what
    // stops somebody walking back to a figure from two hours ago.
    refetchOnWindowFocus: true,
  });
}
