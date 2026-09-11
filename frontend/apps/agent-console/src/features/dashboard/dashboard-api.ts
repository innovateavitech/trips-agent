import { createContext, useContext } from 'react';
import { useQuery } from '@tanstack/react-query';
import type { DashboardOverview } from './types';

/**
 * The dashboard's port — the same shape as `WalletApi`. Screens depend on this,
 * and `main.tsx` decides which adapter stands behind it. Today that is the
 * mock in `mock/`; when the orders endpoints exist (#41, #42), an HTTP adapter
 * using `api` from `src/api/client.ts` replaces it and no component changes.
 */
export interface DashboardApi {
  getOverview(): Promise<DashboardOverview>;
}

const DashboardApiContext = createContext<DashboardApi | null>(null);

export const DashboardApiProvider = DashboardApiContext.Provider;

function useDashboardApi(): DashboardApi {
  const api = useContext(DashboardApiContext);
  if (!api) throw new Error('useDashboardApi must be used inside a <DashboardApiProvider>.');
  return api;
}

export const dashboardKeys = {
  all: ['dashboard'] as const,
  overview: () => [...dashboardKeys.all, 'overview'] as const,
};

export function useDashboardOverview() {
  const api = useDashboardApi();
  return useQuery({
    queryKey: dashboardKeys.overview(),
    queryFn: () => api.getOverview(),
    // Ticket time limits count down; keep the list honest while it is open.
    refetchInterval: 60_000,
  });
}
