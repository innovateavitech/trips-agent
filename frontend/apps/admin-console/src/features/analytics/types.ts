/**
 * Mirrors of the records in
 * backend/services/TripsAgent.Contracts/Analytics/AnalyticsContracts.cs, camel-cased as the API
 * sends them. Hand-written like the rest of this console's types — see
 * features/agencies/types.ts for why, and when they go.
 *
 * Every amount is minor units. Every ratio is basis points, because that is how the server sends
 * it: both sides of the division are exact integers, so the ratio is computed exactly once, on the
 * server, rather than twice in two languages.
 */

export interface PlatformDay {
  day: string;
  currency: string;
  sellingAgencies: number;
  newAgencies: number;
  orders: number;
  bookings: number;
  gmvMinor: number;
  platformFeeMinor: number;
  markupMinor: number;
  refunds: number;
  refundedGrossMinor: number;
  failures: number;
}

export interface PlatformAnalytics {
  from: string;
  to: string;
  /** When the read models were last rebuilt. Null when the window holds nothing. */
  generatedAt: string | null;
  currency: string;
  /** What travellers paid, across every agency. Not Trips' revenue. */
  gmvMinor: number;
  previousGmvMinor: number;
  /** Against the previous window of the same length. Null when that window sold nothing. */
  gmvChangeBasisPoints: number | null;
  /** Trips' own revenue: the fee taken out of agents' margins. */
  platformFeeMinor: number;
  markupMinor: number;
  orders: number;
  bookings: number;
  newAgencies: number;
  activeAgencies: number;
  refunds: number;
  refundedGrossMinor: number;
  failures: number;
  days: PlatformDay[];
}

export interface SupplierPerformanceRow {
  supplierId: string;
  supplierCode: string;
  supplierName: string;
  searches: number;
  priceConfirmations: number;
  issueAttempts: number;
  booked: number;
  statusPolls: number;
  totalCalls: number;
  errors: number;
  /**
   * Counted apart from errors. A timed-out ticket issue is an unknown outcome, not a failure — a
   * ticket may exist — and the answer is always to poll, never to send it again (ADR-0003).
   */
  timeouts: number;
  /** Issued tickets against searches. Null when nobody searched. */
  conversionBasisPoints: number | null;
  /** Failed calls against all calls. Null when no calls were made. */
  errorRateBasisPoints: number | null;
  averageLatencyMs: number;
  maxLatencyMs: number;
}

export interface SupplierDay {
  day: string;
  supplierId: string;
  searches: number;
  booked: number;
  totalCalls: number;
  errors: number;
  averageLatencyMs: number;
}

export interface SupplierPerformance {
  from: string;
  to: string;
  generatedAt: string | null;
  suppliers: SupplierPerformanceRow[];
  days: SupplierDay[];
}

/** One logged export: who took what, how many rows, and when. */
export interface ReportExport {
  id: string;
  reportJobId: string | null;
  definitionCode: string;
  scope: string;
  agencyId: string | null;
  actorUserId: string | null;
  actorType: string;
  scopeDescription: string;
  rowCount: number;
  exportedAt: string;
}

export interface AnalyticsWindow {
  from: string;
  to: string;
}
