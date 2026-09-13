import type { AnalyticsApi } from '../analytics-api';
import { daysCovered, lagosDay, willBeQueued } from '../analytics-rules';
import type {
  AgencyAnalytics,
  AnalyticsDay,
  AnalyticsWindow,
  BookingDrillDown,
  ReportDefinition,
  ReportJob,
} from '../types';

/**
 * ============================================================================
 *  The demo stand-in. `main.tsx` uses it only with VITE_AUTH_MODE=mock; the
 *  real endpoints (issues 67 and 68) are createHttpAnalyticsApi.
 * ============================================================================
 *
 * Generates a plausible month of trading from the day number, so the chart has
 * a shape rather than a flat line and the same day always looks the same. No
 * randomness: a demo that redraws differently on every render is impossible to
 * talk over.
 */

const LATENCY_MS = 250;
const CURRENCY = 'NGN';

const delay = (ms = LATENCY_MS) => new Promise((resolve) => setTimeout(resolve, ms));

const DEFINITIONS: ReportDefinition[] = [
  {
    code: 'agency.sales.daily',
    name: 'Daily sales',
    description: 'One row per day: bookings, gross sales, cost, markup and margin.',
    scope: 'Agency',
    alwaysAsynchronous: false,
    synchronousDayLimit: 90,
  },
  {
    code: 'agency.bookings',
    name: 'Bookings',
    description:
      'One row per booking, with the order, traveller count, status and what was charged.',
    scope: 'Agency',
    alwaysAsynchronous: false,
    synchronousDayLimit: 90,
  },
];

const jobs: ReportJob[] = [];

export const mockAnalyticsApi: AnalyticsApi = {
  async getSummary(window) {
    await delay();
    return summaryFor(window);
  },

  async getBookings(window, page, pageSize) {
    await delay();
    return drillDownFor(window, page, pageSize);
  },

  async listDefinitions() {
    await delay(120);
    return DEFINITIONS;
  },

  async listJobs() {
    await delay(120);
    return [...jobs];
  },

  async runReport(definitionCode, window) {
    await delay(600);

    const definition = DEFINITIONS.find((candidate) => candidate.code === definitionCode);
    const days = daysCovered(window);

    const job: ReportJob = {
      id: crypto.randomUUID(),
      definitionCode,
      scope: 'Agency',
      runMode: definition && willBeQueued(definition, window) ? 'Asynchronous' : 'Synchronous',
      status: 'Succeeded',
      format: 'Csv',
      fromDay: window.from,
      toDay: window.to,
      scopeDescription: `${definition?.name ?? definitionCode}, ${window.from} to ${window.to}, Demo Travel`,
      requestedAt: new Date().toISOString(),
      completedAt: new Date().toISOString(),
      rowCount: days,
      resultSizeBytes: days * 64,
      canDownload: true,
      errorMessage: null,
    };

    jobs.unshift(job);

    if (job.runMode === 'Asynchronous') {
      return { kind: 'queued', job };
    }

    return { kind: 'file', fileName: `${definitionCode}.csv`, blob: csvFor(window) };
  },

  async downloadJob(jobId) {
    await delay(300);

    const job = jobs.find((candidate) => candidate.id === jobId);

    return {
      fileName: `${job?.definitionCode ?? 'report'}.csv`,
      blob: csvFor({
        from: job?.fromDay ?? lagosDay(new Date()),
        to: job?.toDay ?? lagosDay(new Date()),
      }),
    };
  },
};

/** A day's takings, derived from its date so the same day always looks the same. */
function grossFor(day: string): number {
  const seed = Number(day.replaceAll('-', '')) % 97;

  // Sundays are quiet, month ends are busy. Enough shape to be worth drawing.
  const weekday = new Date(`${day}T00:00:00Z`).getUTCDay();
  const base = weekday === 0 ? 40_000_00 : 120_000_00;

  return base + seed * 3_500_00;
}

function summaryFor(window: AnalyticsWindow): AgencyAnalytics {
  const days = daysIn(window).map<AnalyticsDay>((day, index) => {
    const gross = grossFor(day);
    const net = Math.round(gross * 0.88);
    const markup = gross - net - 750_00;

    return {
      day,
      currency: CURRENCY,
      orders: 3 + (index % 4),
      bookings: 4 + (index % 5),
      grossSalesMinor: gross,
      refunds: index % 9 === 0 ? 1 : 0,
      refundedGrossMinor: index % 9 === 0 ? 110_750_00 : 0,
      cancellations: 0,
      failures: index % 11 === 0 ? 1 : 0,
      netCostMinor: net,
      markupMinor: markup,
      marginMinor: markup - 500_00,
    };
  });

  const sum = (pick: (day: AnalyticsDay) => number) =>
    days.reduce((total, day) => total + pick(day), 0);
  const gross = sum((day) => day.grossSalesMinor);
  const previous = Math.round(gross * 0.91);

  return {
    from: window.from,
    to: window.to,
    generatedAt: new Date().toISOString(),
    showsMargin: true,
    totals: {
      currency: CURRENCY,
      orders: sum((day) => day.orders),
      bookings: sum((day) => day.bookings),
      grossSalesMinor: gross,
      refunds: sum((day) => day.refunds),
      refundedGrossMinor: sum((day) => day.refundedGrossMinor),
      cancellations: 0,
      failures: sum((day) => day.failures),
      previousGrossSalesMinor: previous,
      changeBasisPoints:
        previous === 0 ? null : Math.round(((gross - previous) * 10_000) / previous),
      netCostMinor: sum((day) => day.netCostMinor ?? 0),
      markupMinor: sum((day) => day.markupMinor ?? 0),
      marginMinor: sum((day) => day.marginMinor ?? 0),
    },
    days,
    byProductType: [
      {
        label: 'Flight',
        bookings: 61,
        grossSalesMinor: Math.round(gross * 0.72),
        marginMinor: 410_000_00,
      },
      {
        label: 'Bus',
        bookings: 24,
        grossSalesMinor: Math.round(gross * 0.18),
        marginMinor: 96_000_00,
      },
      {
        label: 'Tour',
        bookings: 9,
        grossSalesMinor: Math.round(gross * 0.1),
        marginMinor: 71_000_00,
      },
    ],
    byChannel: [
      {
        label: 'Console',
        bookings: 70,
        grossSalesMinor: Math.round(gross * 0.81),
        marginMinor: 470_000_00,
      },
      {
        label: 'Storefront',
        bookings: 24,
        grossSalesMinor: Math.round(gross * 0.19),
        marginMinor: 107_000_00,
      },
    ],
  };
}

function drillDownFor(window: AnalyticsWindow, page: number, pageSize: number): BookingDrillDown {
  const total = 7;
  const rows = Array.from({ length: total }, (_unused, index) => ({
    orderId: `order-${index}`,
    orderLineId: `line-${index}`,
    orderNumber: `ORD-2026-${String(1_000 + index)}`,
    day: window.from,
    occurredAt: `${window.from}T09:${String(10 + index).padStart(2, '0')}:00Z`,
    itemType: index % 3 === 0 ? 'Bus' : 'Flight',
    channel: index % 4 === 0 ? 'Storefront' : 'Console',
    title: index % 3 === 0 ? 'LOS → IBA, GIGM' : 'LOS → ABV, Air Peace',
    orderStatus: 'Confirmed',
    fulfilmentStatus: index === 5 ? 'FailedNeedsResolution' : 'Confirmed',
    currency: CURRENCY,
    grossAmountMinor: 110_750_00 + index * 5_000_00,
    netAmountMinor: 100_000_00 + index * 4_000_00,
    marginMinor: 9_500_00,
  }));

  return {
    from: window.from,
    to: window.to,
    showsMargin: true,
    total,
    page,
    pageSize,
    rows: rows.slice((page - 1) * pageSize, page * pageSize),
  };
}

function csvFor(window: AnalyticsWindow): Blob {
  const lines = ['"Day","Bookings","Gross sales (NGN)"'];

  for (const day of daysIn(window)) {
    lines.push(`"${day}","5","${(grossFor(day) / 100).toFixed(2)}"`);
  }

  return new Blob([lines.join('\r\n')], { type: 'text/csv;charset=utf-8' });
}

function daysIn(window: AnalyticsWindow): string[] {
  const days: string[] = [];
  const start = Date.parse(`${window.from}T00:00:00Z`);
  const count = daysCovered(window);

  for (let index = 0; index < count; index++) {
    days.push(new Date(start + index * 86_400_000).toISOString().slice(0, 10));
  }

  return days;
}
