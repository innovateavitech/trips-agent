import { Link } from 'react-router-dom';
import {
  Badge,
  Button,
  Skeleton,
  Table,
  TableBody,
  TableCaption,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@trips/ui';
import { formatDateTime } from '../../../lib/format';
import { actionLabel, actionTone, actorTypeDisplay } from '../audit-rules';
import type { AuditLogEntry } from '../types';

/**
 * The trail itself: when, who, what, to whom, and why.
 *
 * "Why" is a column rather than something behind a click, because it is the reason this screen
 * exists — a list of actions without their reasons is a log, not an audit trail. The before and
 * after state is behind the detail button, since it is long and rarely what is being looked for.
 */
export function AuditTable({
  entries,
  selectedId,
  onSelect,
}: {
  entries: AuditLogEntry[];
  selectedId: string | null;
  onSelect: (entry: AuditLogEntry) => void;
}) {
  return (
    <Table>
      <TableCaption className="pb-3">
        Newest first. Nothing here can be edited or deleted, by anybody, including us.
      </TableCaption>

      <TableHeader>
        <TableRow className="hover:bg-transparent">
          <TableHead scope="col">When</TableHead>
          <TableHead scope="col">Who</TableHead>
          <TableHead scope="col">Action</TableHead>
          <TableHead scope="col">On</TableHead>
          <TableHead scope="col">Why</TableHead>
          <TableHead scope="col">
            <span className="sr-only">Detail</span>
          </TableHead>
        </TableRow>
      </TableHeader>

      <TableBody>
        {entries.map((entry) => {
          const actor = actorTypeDisplay(entry.actorType);

          return (
            <TableRow key={entry.id} data-state={entry.id === selectedId ? 'selected' : undefined}>
              <TableCell className="whitespace-nowrap align-top tabular-nums text-muted-foreground">
                {formatDateTime(entry.occurredAt)}
              </TableCell>

              <TableCell className="align-top">
                <p className="font-medium text-foreground">{entry.actorName}</p>
                <p className="text-xs text-muted-foreground">{actor.label}</p>
              </TableCell>

              <TableCell className="align-top">
                <Badge tone={actionTone(entry.action)}>{actionLabel(entry.action)}</Badge>
              </TableCell>

              <TableCell className="align-top">
                {entry.agencyId ? (
                  <Link
                    to={`/agencies/${entry.agencyId}`}
                    className="font-medium text-foreground underline-offset-4 hover:underline"
                  >
                    {entry.agencyName ?? entry.agencyId}
                  </Link>
                ) : (
                  <span className="text-muted-foreground">The platform</span>
                )}
                <p className="text-xs text-muted-foreground">{entry.entityType}</p>
              </TableCell>

              {/* Clamped rather than truncated at a character count: a reason is a sentence, and
                  where it wraps depends on the column, not on a number we guessed. */}
              <TableCell className="min-w-64 max-w-96 align-top text-muted-foreground">
                {entry.reason ? (
                  <span className="line-clamp-3">{entry.reason}</span>
                ) : (
                  <span aria-label="No reason recorded">—</span>
                )}
              </TableCell>

              <TableCell className="align-top text-right">
                <Button variant="outline" size="sm" onClick={() => onSelect(entry)}>
                  Detail
                  <span className="sr-only">
                    {' '}
                    of {actionLabel(entry.action)} on {formatDateTime(entry.occurredAt)}
                  </span>
                </Button>
              </TableCell>
            </TableRow>
          );
        })}
      </TableBody>
    </Table>
  );
}

/** Same footprint as the table, so the page does not jump when the page of entries arrives. */
export function AuditTableSkeleton({ rows = 10 }: { rows?: number }) {
  return (
    <div aria-busy="true" aria-label="Loading the audit trail" className="flex flex-col gap-3 p-4">
      <Skeleton className="h-4 w-1/3" />
      {Array.from({ length: rows }, (_, index) => (
        <Skeleton key={index} className="h-10 w-full" />
      ))}
    </div>
  );
}
