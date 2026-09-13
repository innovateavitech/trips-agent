import { createContext, useContext } from 'react';
import type { ApiClient } from '../../lib/api/client';
import type {
  AnalyticsWindow,
  PlatformAnalytics,
  ReportExport,
  SupplierPerformance,
} from './types';

/**
 * What the platform dashboard needs from the server.
 *
 * Three calls rather than one, unlike the operations dashboard, because these three answer
 * different questions at different rhythms: GMV moves with bookings, supplier behaviour moves with
 * the supplier, and the export log moves only when somebody exports something. Bundling them would
 * mean re-reading all three whenever any one of them changed.
 */
export interface PlatformAnalyticsApi {
  /** GET /api/v1/admin/analytics — GMV, growth and the daily series. */
  getSummary(window: AnalyticsWindow): Promise<PlatformAnalytics>;

  /** GET /api/v1/admin/analytics/suppliers — conversion, errors and latency per supplier. */
  getSuppliers(window: AnalyticsWindow): Promise<SupplierPerformance>;

  /** GET /api/v1/admin/analytics/exports — who exported what, and how many rows. */
  listExports(limit?: number): Promise<ReportExport[]>;
}

export function createHttpPlatformAnalyticsApi(client: ApiClient): PlatformAnalyticsApi {
  const range = (window: AnalyticsWindow) =>
    `from=${encodeURIComponent(window.from)}&to=${encodeURIComponent(window.to)}`;

  return {
    getSummary: (window) =>
      client.get<PlatformAnalytics>(`/api/v1/admin/analytics?${range(window)}`),

    getSuppliers: (window) =>
      client.get<SupplierPerformance>(`/api/v1/admin/analytics/suppliers?${range(window)}`),

    listExports: (limit = 100) =>
      client.get<ReportExport[]>(`/api/v1/admin/analytics/exports?limit=${limit}`),
  };
}

const PlatformAnalyticsApiContext = createContext<PlatformAnalyticsApi | null>(null);

export const PlatformAnalyticsApiProvider = PlatformAnalyticsApiContext.Provider;

export function usePlatformAnalyticsApi(): PlatformAnalyticsApi {
  const api = useContext(PlatformAnalyticsApiContext);
  if (!api) {
    throw new Error(
      'usePlatformAnalyticsApi must be used inside a <PlatformAnalyticsApiProvider>.',
    );
  }
  return api;
}

export const platformAnalyticsKeys = {
  all: ['platform-analytics'] as const,
  summary: (window: AnalyticsWindow) => [...platformAnalyticsKeys.all, 'summary', window] as const,
  suppliers: (window: AnalyticsWindow) =>
    [...platformAnalyticsKeys.all, 'suppliers', window] as const,
  exports: () => [...platformAnalyticsKeys.all, 'exports'] as const,
};
