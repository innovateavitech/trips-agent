import { useMemo, useState } from 'react';
import { SegmentedControl, Skeleton } from '@trips/ui';
import { Page, PageHeader } from '../../../components/page';
import { ErrorState } from '../../../components/states';
import { describeLoadError } from '../../../lib/api/problem';
import { useDocumentTitle } from '../../../lib/hooks';
import { formatDateTime } from '../../../lib/format';
import { DEFAULT_WINDOW_ID, WINDOW_PRESETS, spansYears, windowOf } from '../analytics-rules';
import {
  usePlatformAnalytics,
  useReportExports,
  useSupplierPerformance,
} from '../analytics-queries';
import { ExportsTable } from '../components/exports-table';
import { GmvChart } from '../components/gmv-chart';
import { PlatformTiles, PlatformTilesSkeleton } from '../components/platform-tiles';
import { SupplierTable } from '../components/supplier-table';
import type { AnalyticsWindow } from '../types';

/**
 * The platform's own numbers: GMV and growth, how the suppliers are behaving, and who has taken
 * data out.
 *
 * Read from the analytics read models, never from a live query over orders — a full scan of every
 * agency's bookings is exactly the query nobody wants running while those agencies are booking.
 * The page says when the figures were counted rather than implying they are live.
 */
export function PlatformAnalyticsPage() {
  useDocumentTitle('Platform analytics');

  const [presetId, setPresetId] = useState(DEFAULT_WINDOW_ID);

  const preset =
    WINDOW_PRESETS.find((candidate) => candidate.id === presetId) ?? WINDOW_PRESETS[1]!;
  const window: AnalyticsWindow = useMemo(() => windowOf(preset.days), [preset.days]);

  const summary = usePlatformAnalytics(window);
  const suppliers = useSupplierPerformance(window);
  const exports = useReportExports();

  const withYear = spansYears(window);

  return (
    <Page wide>
      <PageHeader
        title="Platform analytics"
        description="GMV, growth and supplier performance, counted from the read models rather than from the live booking tables."
        actions={
          <SegmentedControl
            label="Window"
            value={presetId}
            onChange={setPresetId}
            options={WINDOW_PRESETS.map((candidate) => ({
              value: candidate.id,
              label: candidate.label,
            }))}
          />
        }
      />

      {summary.isError ? (
        <ErrorState
          {...describeLoadError(summary.error)}
          onRetry={() => void summary.refetch()}
          retrying={summary.isFetching}
        />
      ) : null}

      {summary.isPending ? <PlatformTilesSkeleton /> : null}

      {summary.data ? (
        <>
          <PlatformTiles data={summary.data} />

          <section className="flex flex-col gap-3" aria-labelledby="gmv-heading">
            <div className="flex flex-wrap items-baseline justify-between gap-2">
              <h2 id="gmv-heading" className="text-lg font-semibold tracking-tight text-foreground">
                GMV by day
              </h2>
              <p className="text-xs text-muted-foreground">
                {summary.data.generatedAt
                  ? `Counted ${formatDateTime(summary.data.generatedAt)}`
                  : 'Nothing counted in this window yet'}
              </p>
            </div>

            <GmvChart
              days={summary.data.days}
              currency={summary.data.currency}
              withYear={withYear}
            />
          </section>
        </>
      ) : null}

      <section className="flex flex-col gap-3" aria-labelledby="suppliers-heading">
        <div className="flex flex-wrap items-baseline justify-between gap-2">
          <h2
            id="suppliers-heading"
            className="text-lg font-semibold tracking-tight text-foreground"
          >
            Supplier performance
          </h2>
          <p className="text-xs text-muted-foreground">
            A timeout is an unknown outcome, not a failure — it is counted on its own
          </p>
        </div>

        {suppliers.isError ? (
          <ErrorState
            {...describeLoadError(suppliers.error)}
            onRetry={() => void suppliers.refetch()}
            retrying={suppliers.isFetching}
          />
        ) : null}

        {suppliers.isPending ? <Skeleton className="h-32 w-full" /> : null}
        {suppliers.data ? <SupplierTable rows={suppliers.data.suppliers} /> : null}
      </section>

      <section className="flex flex-col gap-3" aria-labelledby="exports-heading">
        <div className="flex flex-wrap items-baseline justify-between gap-2">
          <h2 id="exports-heading" className="text-lg font-semibold tracking-tight text-foreground">
            Exports
          </h2>
          <p className="text-xs text-muted-foreground">
            Every export is recorded with who took it, what it covered and how many rows
          </p>
        </div>

        {exports.isError ? (
          <ErrorState
            {...describeLoadError(exports.error)}
            onRetry={() => void exports.refetch()}
            retrying={exports.isFetching}
          />
        ) : null}

        {exports.isPending ? <Skeleton className="h-32 w-full" /> : null}
        {exports.data ? <ExportsTable exports={exports.data} /> : null}
      </section>
    </Page>
  );
}
