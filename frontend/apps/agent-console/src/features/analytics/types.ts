/**
 * What the analytics and reports screens work with.
 *
 * Money is always minor units — kobo — as a whole `number`, never a decimal.
 * `formatMoney` from `@trips/utils` is the only thing that turns one into
 * something a person reads.
 *
 * The margin fields are `null` when the signed-in account does not hold
 * `margin.view`. The server leaves them out of the JSON entirely; these types
 * say `null` because that is what a missing property becomes once it is read.
 * `showsMargin` on the summary says which case you are in, so a screen can
 * explain the absence instead of silently showing a shorter table.
 */

export type Money = number;

export interface AnalyticsDay {
  day: string;
  currency: string;
  orders: number;
  bookings: number;
  grossSalesMinor: Money;
  refunds: number;
  refundedGrossMinor: Money;
  cancellations: number;
  failures: number;
  netCostMinor: Money | null;
  markupMinor: Money | null;
  marginMinor: Money | null;
}

export interface AnalyticsTotals {
  currency: string;
  orders: number;
  bookings: number;
  grossSalesMinor: Money;
  refunds: number;
  refundedGrossMinor: Money;
  cancellations: number;
  failures: number;
  previousGrossSalesMinor: Money;
  /** Change against the previous window of the same length, in basis points. */
  changeBasisPoints: number | null;
  netCostMinor: Money | null;
  markupMinor: Money | null;
  marginMinor: Money | null;
}

export interface AnalyticsBreakdown {
  label: string;
  bookings: number;
  grossSalesMinor: Money;
  marginMinor: Money | null;
}

export interface AgencyAnalytics {
  from: string;
  to: string;
  /** When the read models were last rebuilt. Null when the window holds no data. */
  generatedAt: string | null;
  showsMargin: boolean;
  totals: AnalyticsTotals;
  days: AnalyticsDay[];
  byProductType: AnalyticsBreakdown[];
  byChannel: AnalyticsBreakdown[];
}

export interface BookingRow {
  orderId: string;
  orderLineId: string;
  orderNumber: string;
  day: string;
  occurredAt: string;
  itemType: string;
  channel: string;
  title: string;
  orderStatus: string;
  fulfilmentStatus: string;
  currency: string;
  grossAmountMinor: Money;
  netAmountMinor: Money | null;
  marginMinor: Money | null;
}

export interface BookingDrillDown {
  from: string;
  to: string;
  showsMargin: boolean;
  total: number;
  page: number;
  pageSize: number;
  rows: BookingRow[];
}

export interface ReportDefinition {
  code: string;
  name: string;
  description: string;
  scope: string;
  /** True for a report that crosses agencies, which is never answered in the request. */
  alwaysAsynchronous: boolean;
  synchronousDayLimit: number;
}

export type ReportJobStatus = 'Queued' | 'Running' | 'Succeeded' | 'Failed';

export interface ReportJob {
  id: string;
  definitionCode: string;
  scope: string;
  runMode: string;
  status: ReportJobStatus;
  format: string;
  fromDay: string;
  toDay: string;
  scopeDescription: string;
  requestedAt: string;
  completedAt: string | null;
  rowCount: number | null;
  resultSizeBytes: number | null;
  canDownload: boolean;
  errorMessage: string | null;
}

/** What a run produced: a file to save now, or a job that will be emailed. */
export type ReportRunResult =
  { kind: 'file'; fileName: string; blob: Blob } | { kind: 'queued'; job: ReportJob };

export interface AnalyticsWindow {
  from: string;
  to: string;
}
