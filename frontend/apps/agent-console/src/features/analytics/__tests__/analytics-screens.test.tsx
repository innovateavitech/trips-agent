// @vitest-environment jsdom
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AnalyticsApiProvider, type AnalyticsApi } from '../analytics-api';
import { AnalyticsPage } from '../pages/analytics-page';
import { ReportsPage } from '../pages/reports-page';
import type { AgencyAnalytics, BookingDrillDown, ReportDefinition, ReportJob } from '../types';

afterEach(cleanup);

const DAY = '2026-03-10';

function summary(patch: Partial<AgencyAnalytics> = {}): AgencyAnalytics {
  return {
    from: '2026-02-09',
    to: DAY,
    generatedAt: '2026-03-10T09:00:00Z',
    showsMargin: true,
    totals: {
      currency: 'NGN',
      orders: 12,
      bookings: 18,
      grossSalesMinor: 1_995_000_00,
      refunds: 1,
      refundedGrossMinor: 110_750_00,
      cancellations: 0,
      failures: 2,
      previousGrossSalesMinor: 1_500_000_00,
      changeBasisPoints: 3_300,
      netCostMinor: 1_700_000_00,
      markupMinor: 240_000_00,
      marginMinor: 230_000_00,
    },
    days: [
      {
        day: DAY,
        currency: 'NGN',
        orders: 12,
        bookings: 18,
        grossSalesMinor: 1_995_000_00,
        refunds: 1,
        refundedGrossMinor: 110_750_00,
        cancellations: 0,
        failures: 2,
        netCostMinor: 1_700_000_00,
        markupMinor: 240_000_00,
        marginMinor: 230_000_00,
      },
    ],
    byProductType: [
      { label: 'Flight', bookings: 15, grossSalesMinor: 1_800_000_00, marginMinor: 200_000_00 },
    ],
    byChannel: [
      { label: 'Console', bookings: 18, grossSalesMinor: 1_995_000_00, marginMinor: 230_000_00 },
    ],
    ...patch,
  };
}

function drillDown(): BookingDrillDown {
  return {
    from: DAY,
    to: DAY,
    showsMargin: true,
    total: 1,
    page: 1,
    pageSize: 50,
    rows: [
      {
        orderId: 'order-1',
        orderLineId: 'line-1',
        orderNumber: 'ORD-2026-000042',
        day: DAY,
        occurredAt: '2026-03-10T09:15:00Z',
        itemType: 'Flight',
        channel: 'Console',
        title: 'LOS → ABV, Air Peace',
        orderStatus: 'Confirmed',
        fulfilmentStatus: 'Confirmed',
        currency: 'NGN',
        grossAmountMinor: 110_750_00,
        netAmountMinor: 100_000_00,
        marginMinor: 9_500_00,
      },
    ],
  };
}

const DEFINITION: ReportDefinition = {
  code: 'agency.sales.daily',
  name: 'Daily sales',
  description: 'One row per day.',
  scope: 'Agency',
  alwaysAsynchronous: false,
  synchronousDayLimit: 90,
};

function job(patch: Partial<ReportJob> = {}): ReportJob {
  return {
    id: 'job-1',
    definitionCode: 'agency.sales.daily',
    scope: 'Agency',
    runMode: 'Asynchronous',
    status: 'Queued',
    format: 'Csv',
    fromDay: '2025-01-01',
    toDay: '2026-03-10',
    scopeDescription: 'Daily sales, 2025-01-01 to 2026-03-10, Lagos Travel Limited',
    requestedAt: '2026-03-10T09:00:00Z',
    completedAt: null,
    rowCount: null,
    resultSizeBytes: null,
    canDownload: false,
    errorMessage: null,
    ...patch,
  };
}

function api(patch: Partial<AnalyticsApi> = {}): AnalyticsApi {
  return {
    getSummary: () => Promise.resolve(summary()),
    getBookings: () => Promise.resolve(drillDown()),
    listDefinitions: () => Promise.resolve([DEFINITION]),
    listJobs: () => Promise.resolve([]),
    runReport: () => Promise.resolve({ kind: 'queued', job: job() }),
    downloadJob: () => Promise.resolve({ fileName: 'r.csv', blob: new Blob(['a']) }),
    ...patch,
  };
}

function renderWith(element: React.ReactNode, adapter: AnalyticsApi) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });

  return render(
    <QueryClientProvider client={queryClient}>
      <AnalyticsApiProvider value={adapter}>
        <MemoryRouter>{element}</MemoryRouter>
      </AnalyticsApiProvider>
    </QueryClientProvider>,
  );
}

describe('the analytics screen', () => {
  it('shows the margin when the account may see it', async () => {
    renderWith(<AnalyticsPage />, api());

    // ₦230,000.00 — the markup less the platform's fee. The same figure appears
    // in the tile and again in the tables below it, so this asserts the tile.
    expect((await screen.findAllByText(/230,000\.00/)).length).toBeGreaterThan(0);
    expect(screen.getByText('Your margin')).toBeTruthy();
  });

  it('explains the missing margin rather than showing a zero', async () => {
    // The server omits the fields entirely; the screen has to say why, or an
    // agent reads the gap as "we made nothing".
    const withoutMargin = summary({
      showsMargin: false,
      totals: { ...summary().totals, netCostMinor: null, markupMinor: null, marginMinor: null },
      days: [{ ...summary().days[0]!, netCostMinor: null, markupMinor: null, marginMinor: null }],
    });

    renderWith(<AnalyticsPage />, api({ getSummary: () => Promise.resolve(withoutMargin) }));

    expect(
      await screen.findByText(/Only owners and managers can see what a booking cost/),
    ).toBeTruthy();
  });

  it('opens the bookings behind a day when that day is chosen', async () => {
    const user = userEvent.setup();
    renderWith(<AnalyticsPage />, api());

    await user.click(await screen.findByRole('button', { name: /See the bookings behind 10 Mar/ }));

    expect(await screen.findByText('ORD-2026-000042')).toBeTruthy();
  });

  it('asks the server only for the day that was chosen', async () => {
    const user = userEvent.setup();
    const getBookings = vi.fn().mockResolvedValue(drillDown());

    renderWith(<AnalyticsPage />, api({ getBookings }));

    await user.click(await screen.findByRole('button', { name: /See the bookings behind 10 Mar/ }));

    await waitFor(() => expect(getBookings).toHaveBeenCalled());
    expect(getBookings.mock.calls[0]![0]).toEqual({ from: DAY, to: DAY });
  });
});

describe('the reports screen', () => {
  it('says a long report will be emailed before it is run', async () => {
    renderWith(<ReportsPage />, api());

    // The default window is thirty days, so the button offers a download.
    expect(await screen.findByRole('button', { name: 'Download CSV' })).toBeTruthy();
  });

  it('tells the agent where a queued report went', async () => {
    const user = userEvent.setup();

    renderWith(
      <ReportsPage />,
      api({ runReport: () => Promise.resolve({ kind: 'queued', job: job() }) }),
    );

    await user.click(await screen.findByRole('button', { name: 'Download CSV' }));

    expect(await screen.findByText(/We will email you when it is ready/)).toBeTruthy();
  });

  it('shows what a finished export covered', async () => {
    const finished = job({
      status: 'Succeeded',
      rowCount: 434,
      resultSizeBytes: 28_000,
      canDownload: true,
      completedAt: '2026-03-10T09:05:00Z',
    });

    renderWith(<ReportsPage />, api({ listJobs: () => Promise.resolve([finished]) }));

    expect(await screen.findByText('434')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Download' })).toBeTruthy();
  });
});
