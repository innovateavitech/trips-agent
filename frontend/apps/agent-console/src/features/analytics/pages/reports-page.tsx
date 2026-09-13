import { useMemo, useState } from 'react';
import { Alert, ErrorState, Skeleton } from '@trips/ui';
import { ApiError } from '../../../api/errors';
import { windowOf } from '../analytics-rules';
import {
  useDownloadReport,
  useReportDefinitions,
  useReportJobs,
  useRunReport,
} from '../analytics-queries';
import { ReportJobsTable } from '../components/report-jobs-table';
import { ReportRunner } from '../components/report-runner';
import { saveBlob } from '../download';
import type { AnalyticsWindow } from '../types';

/**
 * FRD §2.15 UC-1C — run a report, and collect the ones that took a while.
 *
 * The screen never decides whether a report runs now or in the background: it
 * asks, and the server answers with either a file or a queued run. What it does
 * do is say which one to expect before the button is pressed, because an agent
 * who expects a download and gets an email has been surprised by their own
 * software.
 */
export function ReportsPage() {
  const [window, setWindow] = useState<AnalyticsWindow>(() => windowOf(30));
  const [queuedMessage, setQueuedMessage] = useState<string | null>(null);
  const [downloadingId, setDownloadingId] = useState<string | null>(null);

  const definitions = useReportDefinitions();
  const jobs = useReportJobs();
  const run = useRunReport();
  const download = useDownloadReport();

  const runError = useMemo(() => messageOf(run.error), [run.error]);
  const downloadError = useMemo(() => messageOf(download.error), [download.error]);

  function onRun(definitionCode: string) {
    setQueuedMessage(null);

    run.mutate(
      { definitionCode, window },
      {
        onSuccess: (result) => {
          if (result.kind === 'file') {
            saveBlob(result.blob, result.fileName);
            return;
          }

          setQueuedMessage(
            `${result.job.scopeDescription} is being produced. We will email you when it is ready, and it will appear below.`,
          );
        },
      },
    );
  }

  function onDownload(jobId: string) {
    setDownloadingId(jobId);

    download.mutate(jobId, {
      onSuccess: (result) => saveBlob(result.blob, result.fileName),
      onSettled: () => setDownloadingId(null),
    });
  }

  return (
    <div className="flex flex-col gap-6">
      <header className="flex flex-col gap-1">
        <h1 className="text-2xl font-semibold tracking-tight text-foreground">Reports</h1>
        <p className="text-sm text-muted-foreground">
          Export your sales and bookings as a spreadsheet. Every export is recorded.
        </p>
      </header>

      {definitions.isError ? (
        <ErrorState
          title="We could not load the reports"
          onRetry={() => void definitions.refetch()}
        />
      ) : null}

      {definitions.isPending ? <Skeleton className="h-44 w-full" /> : null}

      {definitions.data ? (
        <ReportRunner
          definitions={definitions.data}
          window={window}
          onWindowChange={setWindow}
          onRun={onRun}
          isRunning={run.isPending}
          error={runError}
        />
      ) : null}

      {queuedMessage ? (
        <Alert tone="success" title="Your report is on its way">
          {queuedMessage}
        </Alert>
      ) : null}

      {downloadError ? (
        <Alert tone="destructive" title="That file could not be downloaded">
          {downloadError}
        </Alert>
      ) : null}

      <section className="flex flex-col gap-3" aria-labelledby="runs-heading">
        <h2 id="runs-heading" className="text-lg font-semibold tracking-tight text-foreground">
          Recent exports
        </h2>

        {jobs.isPending ? <Skeleton className="h-32 w-full" /> : null}

        {jobs.data ? (
          <ReportJobsTable jobs={jobs.data} onDownload={onDownload} downloadingId={downloadingId} />
        ) : null}
      </section>
    </div>
  );
}

/** The API's own sentence where there is one, and something honest where there is not. */
function messageOf(error: unknown): string | null {
  if (error === null || error === undefined) return null;
  if (error instanceof ApiError) return error.detail ?? error.title;
  return 'Something went wrong. Try again in a moment.';
}
