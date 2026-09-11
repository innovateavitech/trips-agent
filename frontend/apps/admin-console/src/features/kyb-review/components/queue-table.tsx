import { Link } from 'react-router-dom';
import {
  Badge,
  Skeleton,
  Table,
  TableBody,
  TableCaption,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
  buttonVariants,
} from '@trips/ui';
import { countryName, formatDateTime } from '../../../lib/format';
import { REVIEW_TARGET_HOURS, waitingTime, type RankedQueueItem } from '../queue-rules';
import { submissionStatusDisplay } from '../status-display';

/**
 * The queue itself. "Waiting" sits right after the agency's name because it is the column that
 * decides what to do next: the line is first-in, first-out, and the colour says how close each
 * agency is to the 48-hour review target.
 */
export function QueueTable({ items, now }: { items: RankedQueueItem[]; now: number }) {
  return (
    <Table>
      <TableCaption className="pb-3">
        Oldest first. Past {REVIEW_TARGET_HOURS} hours an agency is over the review target and
        raises an alert.
      </TableCaption>

      <TableHeader>
        <TableRow className="hover:bg-transparent">
          <TableHead scope="col" className="w-12 text-right">
            <span aria-hidden="true">#</span>
            <span className="sr-only">Place in queue</span>
          </TableHead>
          <TableHead scope="col">Agency</TableHead>
          <TableHead scope="col">Waiting</TableHead>
          <TableHead scope="col">Submitted</TableHead>
          <TableHead scope="col" className="text-right">
            Documents
          </TableHead>
          <TableHead scope="col">Status</TableHead>
          <TableHead scope="col">
            <span className="sr-only">Actions</span>
          </TableHead>
        </TableRow>
      </TableHeader>

      <TableBody>
        {items.map((item) => {
          const waiting = waitingTime(item.submittedAt, now);
          const status = submissionStatusDisplay(item.status);
          const href = `/kyb/${item.submissionId}`;

          return (
            <TableRow key={item.submissionId}>
              <TableCell className="text-right tabular-nums text-muted-foreground">
                {item.position}
              </TableCell>

              <TableCell className="min-w-48">
                <Link
                  to={href}
                  className="font-medium text-foreground underline-offset-4 hover:underline"
                >
                  {item.agencyName}
                </Link>
                <p className="text-xs text-muted-foreground">{countryName(item.countryCode)}</p>
              </TableCell>

              <TableCell className="whitespace-nowrap">
                <Badge tone={waiting.tone}>{waiting.label}</Badge>
                {waiting.overdue ? <span className="sr-only">, over the review target</span> : null}
              </TableCell>

              <TableCell className="whitespace-nowrap tabular-nums text-muted-foreground">
                {formatDateTime(item.submittedAt)}
              </TableCell>

              <TableCell className="text-right tabular-nums">{item.documentCount}</TableCell>

              <TableCell>
                <Badge tone={status.tone}>{status.label}</Badge>
              </TableCell>

              <TableCell className="text-right">
                <Link to={href} className={buttonVariants({ variant: 'outline', size: 'sm' })}>
                  Review<span className="sr-only"> {item.agencyName}</span>
                </Link>
              </TableCell>
            </TableRow>
          );
        })}
      </TableBody>
    </Table>
  );
}

/** Same footprint as the table, so the page does not jump when the queue arrives. */
export function QueueTableSkeleton({ rows = 5 }: { rows?: number }) {
  return (
    <div aria-busy="true" aria-label="Loading the queue" className="flex flex-col gap-3 p-4">
      <Skeleton className="h-4 w-1/3" />
      {Array.from({ length: rows }, (_, index) => (
        <Skeleton key={index} className="h-10 w-full" />
      ))}
    </div>
  );
}
