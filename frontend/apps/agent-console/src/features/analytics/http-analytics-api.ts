import type { ApiClient } from '@trips/api-client';
import { API_BASE_URL } from '../../api/config';
import { ApiError, unwrap } from '../../api/errors';
import { int64 } from '../../api/int64';
import type { AnalyticsApi } from './analytics-api';
import type {
  AgencyAnalytics,
  AnalyticsBreakdown,
  AnalyticsDay,
  AnalyticsTotals,
  AnalyticsWindow,
  BookingDrillDown,
  BookingRow,
  ReportDefinition,
  ReportJob,
  ReportJobStatus,
} from './types';

/**
 * The analytics and reports screens against the real API (issues 67 and 68).
 *
 *   getSummary      → GET  /api/v1/analytics/summary
 *   getBookings     → GET  /api/v1/analytics/bookings
 *   listDefinitions → GET  /api/v1/reports/definitions
 *   runReport       → POST /api/v1/reports
 *   listJobs        → GET  /api/v1/reports/jobs
 *   downloadJob     → GET  /api/v1/reports/jobs/{id}/download
 *
 * The margin figures are simply absent from the JSON for an account without
 * `margin.view`, so reading them yields `undefined`; they are normalised to
 * `null` here so a component has one case to handle rather than two.
 *
 * Running and downloading a report are the two calls the generated client
 * cannot express, because they answer with a file rather than JSON. They use
 * the session transport directly for exactly that reason — see `runFetch`.
 */
export function createHttpAnalyticsApi({
  api,
  send,
}: {
  api: ApiClient;

  /**
   * The session transport's `authFetch`. It takes a `Request` rather than a URL
   * because it may have to clone and resend one after refreshing the token.
   */
  send: (request: Request) => Promise<Response>;
}): AnalyticsApi {
  return {
    async getSummary(window) {
      return toSummary(
        await unwrap(api.GET('/api/v1/analytics/summary', { params: { query: queryOf(window) } })),
      );
    },

    async getBookings(window, page, pageSize) {
      return toDrillDown(
        await unwrap(
          api.GET('/api/v1/analytics/bookings', {
            params: { query: { ...queryOf(window), page, pageSize } },
          }),
        ),
      );
    },

    async listDefinitions() {
      return (await unwrap(api.GET('/api/v1/reports/definitions'))).map(toDefinition);
    },

    async listJobs() {
      return (await unwrap(api.GET('/api/v1/reports/jobs'))).map(toJob);
    },

    async runReport(definitionCode, window) {
      const response = await send(
        new Request(`${API_BASE_URL}/api/v1/reports`, {
          method: 'POST',
          headers: { 'content-type': 'application/json' },
          body: JSON.stringify({ definitionCode, from: window.from, to: window.to }),
        }),
      );

      if (!response.ok) {
        throw await problemFrom(response);
      }

      // 202 means it was queued: the body is the run to watch, not a file.
      if (response.status === 202) {
        return { kind: 'queued', job: toJob(await response.json()) };
      }

      return {
        kind: 'file',
        fileName: fileNameOf(response) ?? `${definitionCode}.csv`,
        blob: await response.blob(),
      };
    },

    async downloadJob(jobId) {
      const response = await send(
        new Request(`${API_BASE_URL}/api/v1/reports/jobs/${jobId}/download`),
      );

      if (!response.ok) {
        throw await problemFrom(response);
      }

      return { fileName: fileNameOf(response) ?? 'report.csv', blob: await response.blob() };
    },
  };
}

function queryOf(window: AnalyticsWindow) {
  return { from: window.from, to: window.to };
}

/** The name the server asked the browser to save the file as. */
function fileNameOf(response: Response): string | null {
  const disposition = response.headers.get('content-disposition');
  const match = disposition?.match(/filename\*?=(?:UTF-8'')?"?([^";]+)"?/i);
  return match?.[1] ? decodeURIComponent(match[1]) : null;
}

/**
 * Turns a non-2xx file response into the same `ApiError` the JSON path throws.
 *
 * The body may be ProblemDetails or may be nothing at all, so it is read
 * defensively: a download that fails should say something useful, not throw a
 * parse error on top of the original problem.
 */
async function problemFrom(response: Response): Promise<ApiError> {
  let title = response.statusText || 'The report could not be produced.';
  let detail: string | undefined;

  try {
    const body: unknown = await response.json();

    if (body !== null && typeof body === 'object') {
      const problem = body as { title?: string; detail?: string; message?: string };
      title = problem.title ?? problem.message ?? title;
      detail = problem.detail;
    }
  } catch {
    // Not JSON. The status alone is what we have to go on.
  }

  return new ApiError(response.status, title, detail);
}

/**
 * How the API's integers arrive.
 *
 * The generated schema types every integer `number | string`, because the
 * server's JSON reader also accepts one written as a string. `int64` reads
 * either and refuses anything that is not a whole, safe number — which is the
 * behaviour we want for a count as much as for an amount of money.
 */
type Int64 = number | string;

/** A 64-bit field that may be absent because the caller may not see it. */
function optionalInt64(value: Int64 | null | undefined): number | null {
  return value === null || value === undefined ? null : int64(value);
}

function toSummary(dto: SummaryDto): AgencyAnalytics {
  return {
    from: dto.from,
    to: dto.to,
    generatedAt: dto.generatedAt ?? null,
    showsMargin: dto.showsMargin,
    totals: toTotals(dto.totals),
    days: dto.days.map(toDay),
    byProductType: dto.byProductType.map(toBreakdown),
    byChannel: dto.byChannel.map(toBreakdown),
  };
}

function toTotals(dto: TotalsDto): AnalyticsTotals {
  return {
    currency: dto.currency,
    orders: int64(dto.orders),
    bookings: int64(dto.bookings),
    grossSalesMinor: int64(dto.grossSalesMinor),
    refunds: int64(dto.refunds),
    refundedGrossMinor: int64(dto.refundedGrossMinor),
    cancellations: int64(dto.cancellations),
    failures: int64(dto.failures),
    previousGrossSalesMinor: int64(dto.previousGrossSalesMinor),
    changeBasisPoints: optionalInt64(dto.changeBasisPoints),
    netCostMinor: optionalInt64(dto.netCostMinor),
    markupMinor: optionalInt64(dto.markupMinor),
    marginMinor: optionalInt64(dto.marginMinor),
  };
}

function toDay(dto: DayDto): AnalyticsDay {
  return {
    day: dto.day,
    currency: dto.currency,
    orders: int64(dto.orders),
    bookings: int64(dto.bookings),
    grossSalesMinor: int64(dto.grossSalesMinor),
    refunds: int64(dto.refunds),
    refundedGrossMinor: int64(dto.refundedGrossMinor),
    cancellations: int64(dto.cancellations),
    failures: int64(dto.failures),
    netCostMinor: optionalInt64(dto.netCostMinor),
    markupMinor: optionalInt64(dto.markupMinor),
    marginMinor: optionalInt64(dto.marginMinor),
  };
}

function toBreakdown(dto: BreakdownDto): AnalyticsBreakdown {
  return {
    label: dto.label,
    bookings: int64(dto.bookings),
    grossSalesMinor: int64(dto.grossSalesMinor),
    marginMinor: optionalInt64(dto.marginMinor),
  };
}

function toDrillDown(dto: DrillDownDto): BookingDrillDown {
  return {
    from: dto.from,
    to: dto.to,
    showsMargin: dto.showsMargin,
    total: int64(dto.total),
    page: int64(dto.page),
    pageSize: int64(dto.pageSize),
    rows: dto.rows.map(toBookingRow),
  };
}

function toBookingRow(dto: BookingRowDto): BookingRow {
  return {
    orderId: dto.orderId,
    orderLineId: dto.orderLineId,
    orderNumber: dto.orderNumber,
    day: dto.day,
    occurredAt: dto.occurredAt,
    itemType: dto.itemType,
    channel: dto.channel,
    title: dto.title,
    orderStatus: dto.orderStatus,
    fulfilmentStatus: dto.fulfilmentStatus,
    currency: dto.currency,
    grossAmountMinor: int64(dto.grossAmountMinor),
    netAmountMinor: optionalInt64(dto.netAmountMinor),
    marginMinor: optionalInt64(dto.marginMinor),
  };
}

function toDefinition(dto: DefinitionDto): ReportDefinition {
  return {
    code: dto.code,
    name: dto.name,
    description: dto.description,
    scope: dto.scope,
    alwaysAsynchronous: dto.alwaysAsynchronous,
    synchronousDayLimit: int64(dto.synchronousDayLimit),
  };
}

function toJob(dto: JobDto): ReportJob {
  return {
    id: dto.id,
    definitionCode: dto.definitionCode,
    scope: dto.scope,
    runMode: dto.runMode,
    status: dto.status as ReportJobStatus,
    format: dto.format,
    fromDay: dto.fromDay,
    toDay: dto.toDay,
    scopeDescription: dto.scopeDescription,
    requestedAt: dto.requestedAt,
    completedAt: dto.completedAt ?? null,
    rowCount: optionalInt64(dto.rowCount),
    resultSizeBytes: optionalInt64(dto.resultSizeBytes),
    canDownload: dto.canDownload,
    errorMessage: dto.errorMessage ?? null,
  };
}

// The shapes the API answers with. Written out rather than taken from the
// generated schema because two of these endpoints answer with a file, so the
// feature already reads its own responses and one source of truth for the
// shapes is clearer than two.
interface TotalsDto {
  currency: string;
  orders: Int64;
  bookings: Int64;
  grossSalesMinor: Int64;
  refunds: Int64;
  refundedGrossMinor: Int64;
  cancellations: Int64;
  failures: Int64;
  previousGrossSalesMinor: Int64;
  changeBasisPoints?: Int64 | null;
  netCostMinor?: Int64 | null;
  markupMinor?: Int64 | null;
  marginMinor?: Int64 | null;
}

interface DayDto {
  day: string;
  currency: string;
  orders: Int64;
  bookings: Int64;
  grossSalesMinor: Int64;
  refunds: Int64;
  refundedGrossMinor: Int64;
  cancellations: Int64;
  failures: Int64;
  netCostMinor?: Int64 | null;
  markupMinor?: Int64 | null;
  marginMinor?: Int64 | null;
}

interface BreakdownDto {
  label: string;
  bookings: Int64;
  grossSalesMinor: Int64;
  marginMinor?: Int64 | null;
}

interface SummaryDto {
  from: string;
  to: string;
  generatedAt?: string | null;
  showsMargin: boolean;
  totals: TotalsDto;
  days: DayDto[];
  byProductType: BreakdownDto[];
  byChannel: BreakdownDto[];
}

interface BookingRowDto {
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
  grossAmountMinor: Int64;
  netAmountMinor?: Int64 | null;
  marginMinor?: Int64 | null;
}

interface DrillDownDto {
  from: string;
  to: string;
  showsMargin: boolean;
  total: Int64;
  page: Int64;
  pageSize: Int64;
  rows: BookingRowDto[];
}

interface DefinitionDto {
  code: string;
  name: string;
  description: string;
  scope: string;
  alwaysAsynchronous: boolean;
  synchronousDayLimit: Int64;
}

interface JobDto {
  id: string;
  definitionCode: string;
  scope: string;
  runMode: string;
  status: string;
  format: string;
  fromDay: string;
  toDay: string;
  scopeDescription: string;
  requestedAt: string;
  completedAt?: string | null;
  rowCount?: Int64 | null;
  resultSizeBytes?: Int64 | null;
  canDownload: boolean;
  errorMessage?: string | null;
}
