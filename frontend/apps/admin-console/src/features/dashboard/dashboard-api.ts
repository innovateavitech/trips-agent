import { createContext, useContext } from 'react';
import type { ApiClient } from '../../lib/api/client';
import type { OperationsDashboard } from './types';

/**
 * What the front page needs from the server.
 *
 * One call. Everything on the page is counted in one request on the server so the numbers agree
 * with each other — five separate requests would each be true at a slightly different moment, and
 * a dashboard whose figures disagree is worse than no dashboard.
 */
export interface DashboardApi {
  /** GET /api/v1/admin/dashboard. `refresh` skips the server's five-minute cache. */
  get(refresh?: boolean): Promise<OperationsDashboard>;
}

export function createHttpDashboardApi(client: ApiClient): DashboardApi {
  return {
    get: (refresh) =>
      client.get<OperationsDashboard>(`/api/v1/admin/dashboard${refresh ? '?refresh=true' : ''}`),
  };
}

const DashboardApiContext = createContext<DashboardApi | null>(null);

export const DashboardApiProvider = DashboardApiContext.Provider;

export function useDashboardApi(): DashboardApi {
  const api = useContext(DashboardApiContext);
  if (!api) throw new Error('useDashboardApi must be used inside a <DashboardApiProvider>.');
  return api;
}

export const dashboardKeys = {
  all: ['dashboard'] as const,
};
