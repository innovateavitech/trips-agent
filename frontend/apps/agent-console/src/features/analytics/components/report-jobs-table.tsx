import {
  Badge,
  Button,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@trips/ui';
import { formatSize } from '../analytics-rules';
import type { ReportJob, ReportJobStatus } from '../types';

const STATUS_TONE: Record<ReportJobStatus, 'neutral' | 'primary' | 'success' | 'destructive'> = {
  Queued: 'neutral',
  Running: 'primary',
  Succeeded: 'success',
  Failed: 'destructive',
};

/**
 * Every report this agency has run recently, whether it took a moment or an hour.
 *
 * A synchronous run is in here too. It costs one row and it means "what did we
 * export last month" has one answer rather than two.
 */
export function ReportJobsTable({
  jobs,
  onDownload,
  downloadingId,
}: {
  jobs: ReportJob[];
  onDownload: (jobId: string) => void;
  downloadingId: string | null;
}) {
  if (jobs.length === 0) {
    return (
      <p className="text-sm text-muted-foreground">
        Nothing has been exported yet. Reports you run appear here, with how many rows each one
        covered.
      </p>
    );
  }

  return (
    <Table>
      <TableHeader>
        <TableRow>
          <TableHead>Report</TableHead>
          <TableHead>Requested</TableHead>
          <TableHead>Status</TableHead>
          <TableHead className="text-right">Rows</TableHead>
          <TableHead className="text-right">Size</TableHead>
          <TableHead className="w-28" />
        </TableRow>
      </TableHeader>

      <TableBody>
        {jobs.map((job) => (
          <TableRow key={job.id}>
            <TableCell>
              <span className="block font-medium text-foreground">{job.scopeDescription}</span>
              {job.errorMessage ? (
                <span className="text-xs text-destructive">{job.errorMessage}</span>
              ) : (
                <span className="text-xs text-muted-foreground">
                  {job.runMode === 'Asynchronous'
                    ? 'Produced in the background'
                    : 'Downloaded live'}
                </span>
              )}
            </TableCell>

            <TableCell className="whitespace-nowrap text-muted-foreground">
              {new Date(job.requestedAt).toLocaleString('en-GB', {
                dateStyle: 'medium',
                timeStyle: 'short',
              })}
            </TableCell>

            <TableCell>
              <Badge tone={STATUS_TONE[job.status]}>{job.status}</Badge>
            </TableCell>

            <TableCell className="text-right tabular-nums">
              {job.rowCount === null ? '—' : job.rowCount.toLocaleString('en-NG')}
            </TableCell>

            <TableCell className="text-right tabular-nums">
              {formatSize(job.resultSizeBytes)}
            </TableCell>

            <TableCell className="text-right">
              {job.canDownload ? (
                <Button
                  variant="outline"
                  size="sm"
                  disabled={downloadingId === job.id}
                  onClick={() => onDownload(job.id)}
                >
                  {downloadingId === job.id ? 'Saving…' : 'Download'}
                </Button>
              ) : null}
            </TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}
