import { useMemo, useState } from 'react';
import { ErrorState, SegmentedControl, Skeleton } from '@trips/ui';
import {
  DEFAULT_WINDOW_ID,
  WINDOW_PRESETS,
  formatDay,
  spansYears,
  windowOf,
} from '../analytics-rules';
import { useAgencyAnalytics, useBookingDrillDown } from '../analytics-queries';
import { BookingsTable } from '../components/bookings-table';
import { BreakdownTable } from '../components/breakdown-table';
import { DailyTable } from '../components/daily-table';
import { SalesChart } from '../components/sales-chart';
import { StatTiles, StatTilesSkeleton } from '../components/stat-tiles';
import type { AnalyticsWindow } from '../types';

/**
 * FRD §2.15 — what this agency sold, what it earned, and what it kept.
 *
 * Every number here comes from the analytics read models, never from a live
 * query over orders, so opening this page while the agency is booking does not
 * compete with the booking. The trade is that the figures are up to ten minutes
 * old, which the page says out loud rather than implying they are live.
 */
export function AnalyticsPage() {
  const [presetId, setPresetId] = useState(DEFAULT_WINDOW_ID);
  const [drillDownDay, setDrillDownDay] = useState<string | null>(null);
  const [page, setPage] = useState(1);

  const preset =
    WINDOW_PRESETS.find((candidate) => candidate.id === presetId) ?? WINDOW_PRESETS[1]!;

  // Recomputed only when the preset changes, so the query key is stable and the
  // page does not refetch on every render.
  const window: AnalyticsWindow = useMemo(() => windowOf(preset.days), [preset.days]);

  const drillDownWindow: AnalyticsWindow = useMemo(
    () => (drillDownDay ? { from: drillDownDay, to: drillDownDay } : window),
    [drillDownDay, window],
  );

  const summary = useAgencyAnalytics(window);
  const bookings = useBookingDrillDown(drillDownWindow, page, drillDownDay !== null);

  const withYear = spansYears(window);

  function selectDay(day: string) {
    setDrillDownDay(day);
    setPage(1);
  }

  return (
    <div className="flex flex-col gap-6">
      <header className="flex flex-wrap items-end justify-between gap-4">
        <div className="flex flex-col gap-1">
          <h1 className="text-2xl font-semibold tracking-tight text-foreground">Analytics</h1>
          <p className="text-sm text-muted-foreground">
            What you sold, what it earned you, and where it came from.
          </p>
        </div>

        <SegmentedControl
          label="Window"
          value={presetId}
          onChange={(next) => {
            setPresetId(next);
            setDrillDownDay(null);
            setPage(1);
          }}
          options={WINDOW_PRESETS.map((candidate) => ({
            value: candidate.id,
            label: candidate.label,
          }))}
        />
      </header>

      {summary.isError ? (
        <ErrorState
          title="We could not load your analytics"
          detail="Nothing is wrong with your bookings — this is a problem reading the summary of them."
          onRetry={() => void summary.refetch()}
        />
      ) : null}

      {summary.isPending ? (
        <>
          <StatTilesSkeleton />
          <Skeleton className="h-48 w-full" />
        </>
      ) : null}

      {summary.data ? (
        <>
          <StatTiles totals={summary.data.totals} showsMargin={summary.data.showsMargin} />

          <section className="flex flex-col gap-3" aria-labelledby="sales-heading">
            <div className="flex flex-wrap items-baseline justify-between gap-2">
              <h2
                id="sales-heading"
                className="text-lg font-semibold tracking-tight text-foreground"
              >
                Sales by day
              </h2>
              <p className="text-xs text-muted-foreground">
                {summary.data.generatedAt
                  ? `Counted ${new Date(summary.data.generatedAt).toLocaleString('en-GB', {
                      dateStyle: 'medium',
                      timeStyle: 'short',
                    })} — refreshed every few minutes`
                  : 'Refreshed every few minutes'}
              </p>
            </div>

            <SalesChart
              days={summary.data.days}
              currency={summary.data.totals.currency}
              withYear={withYear}
              onSelectDay={selectDay}
            />
          </section>

          <section className="grid gap-6 lg:grid-cols-2" aria-label="Breakdowns">
            <BreakdownTable
              caption="Product"
              rows={summary.data.byProductType}
              currency={summary.data.totals.currency}
              showsMargin={summary.data.showsMargin}
            />
            <BreakdownTable
              caption="Channel"
              rows={summary.data.byChannel}
              currency={summary.data.totals.currency}
              showsMargin={summary.data.showsMargin}
            />
          </section>

          <section className="flex flex-col gap-3" aria-labelledby="daily-heading">
            <h2 id="daily-heading" className="text-lg font-semibold tracking-tight text-foreground">
              Every day in the window
            </h2>

            {summary.data.days.length === 0 ? (
              <p className="text-sm text-muted-foreground">
                Nothing sold between {formatDay(window.from, withYear)} and{' '}
                {formatDay(window.to, withYear)}.
              </p>
            ) : (
              <DailyTable
                days={summary.data.days}
                currency={summary.data.totals.currency}
                showsMargin={summary.data.showsMargin}
                withYear={withYear}
                onSelectDay={selectDay}
              />
            )}
          </section>
        </>
      ) : null}

      {drillDownDay ? (
        <section className="flex flex-col gap-3" aria-labelledby="drilldown-heading">
          <div className="flex flex-wrap items-baseline justify-between gap-2">
            <h2
              id="drilldown-heading"
              className="text-lg font-semibold tracking-tight text-foreground"
            >
              Bookings on {formatDay(drillDownDay, withYear)}
            </h2>

            <button
              type="button"
              className="text-sm text-primary underline-offset-4 hover:underline"
              onClick={() => setDrillDownDay(null)}
            >
              Close
            </button>
          </div>

          {bookings.isError ? (
            <ErrorState
              title="We could not load those bookings"
              onRetry={() => void bookings.refetch()}
            />
          ) : null}

          {bookings.data ? (
            <BookingsTable
              data={bookings.data}
              currency={summary.data?.totals.currency ?? 'NGN'}
              withYear={withYear}
              onPage={setPage}
            />
          ) : (
            <Skeleton className="h-40 w-full" />
          )}
        </section>
      ) : null}
    </div>
  );
}
