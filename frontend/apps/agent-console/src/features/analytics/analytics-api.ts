import { createContext, useContext } from 'react';
import type {
  AgencyAnalytics,
  AnalyticsWindow,
  BookingDrillDown,
  ReportDefinition,
  ReportJob,
  ReportRunResult,
} from './types';

/**
 * Everything the analytics and reports screens need from the server.
 *
 * A port, like every other feature here, so the screens never touch `fetch` and
 * a test can hand them an object literal.
 */
export interface AnalyticsApi {
  /** GET /api/v1/analytics/summary — totals, the daily series and the breakdowns. */
  getSummary(window: AnalyticsWindow): Promise<AgencyAnalytics>;

  /**
   * GET /api/v1/analytics/bookings — the bookings behind an aggregate.
   *
   * The drill-down. Driven from the same read model the totals come from, so
   * the rows on this page add up to the number that was clicked on.
   */
  getBookings(window: AnalyticsWindow, page: number, pageSize: number): Promise<BookingDrillDown>;

  /** GET /api/v1/reports/definitions — the reports this account may run. */
  listDefinitions(): Promise<ReportDefinition[]>;

  /**
   * POST /api/v1/reports — runs a report.
   *
   * Comes back either as the file itself or as a queued job, and which one is
   * the server's decision, not a parameter. Over ninety days or across
   * agencies and it is queued; the agent is emailed when it is ready.
   */
  runReport(definitionCode: string, window: AnalyticsWindow): Promise<ReportRunResult>;

  /** GET /api/v1/reports/jobs — recent runs, newest first. */
  listJobs(): Promise<ReportJob[]>;

  /** GET /api/v1/reports/jobs/{id}/download — a finished file. The download is logged. */
  downloadJob(jobId: string): Promise<{ fileName: string; blob: Blob }>;
}

const AnalyticsApiContext = createContext<AnalyticsApi | null>(null);

export const AnalyticsApiProvider = AnalyticsApiContext.Provider;

export function useAnalyticsApi(): AnalyticsApi {
  const api = useContext(AnalyticsApiContext);
  if (!api) {
    throw new Error('useAnalyticsApi must be used inside an <AnalyticsApiProvider>.');
  }
  return api;
}

/**
 * Query keys.
 *
 * `['analytics']` clears both the dashboard and the drill-down, which is right:
 * they are two views of the same read model and cannot be allowed to disagree.
 * Report jobs are their own subtree, because running one does not change any
 * number on the dashboard.
 */
export const analyticsKeys = {
  all: ['analytics'] as const,
  summary: (window: AnalyticsWindow) => [...analyticsKeys.all, 'summary', window] as const,
  bookings: (window: AnalyticsWindow, page: number) =>
    [...analyticsKeys.all, 'bookings', window, page] as const,
  reports: ['reports'] as const,
  definitions: () => ['reports', 'definitions'] as const,
  jobs: () => ['reports', 'jobs'] as const,
};
