import { formatMoney } from '@trips/utils';
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
import { Link } from 'react-router-dom';
import { formatDay } from '../analytics-rules';
import type { BookingDrillDown } from '../types';

/**
 * The bookings behind an aggregate.
 *
 * Each row links to the booking itself, so the path from "March was ₦4.2m" to
 * "this is the ticket" is two clicks. The rows come from the same read model
 * the total does, so they add up to it.
 */
export function BookingsTable({
  data,
  currency,
  withYear,
  onPage,
}: {
  data: BookingDrillDown;
  currency: string;
  withYear: boolean;
  onPage: (page: number) => void;
}) {
  const lastPage = Math.max(1, Math.ceil(data.total / data.pageSize));
  const first = (data.page - 1) * data.pageSize + 1;
  const last = Math.min(data.page * data.pageSize, data.total);

  return (
    <div className="flex flex-col gap-3">
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>Day</TableHead>
            <TableHead>Order</TableHead>
            <TableHead>Item</TableHead>
            <TableHead>Status</TableHead>
            <TableHead className="text-right">Charged</TableHead>
            {data.showsMargin ? <TableHead className="text-right">Margin</TableHead> : null}
          </TableRow>
        </TableHeader>

        <TableBody>
          {data.rows.map((row) => (
            <TableRow key={row.orderLineId}>
              <TableCell className="whitespace-nowrap">{formatDay(row.day, withYear)}</TableCell>
              <TableCell>
                <Link
                  to={`/bookings/${row.orderNumber}`}
                  className="font-medium text-primary underline-offset-4 hover:underline"
                >
                  {row.orderNumber}
                </Link>
              </TableCell>
              <TableCell>
                <span className="block text-foreground">{row.title}</span>
                <span className="text-xs text-muted-foreground">
                  {row.itemType} · {row.channel}
                </span>
              </TableCell>
              <TableCell>
                <Badge tone={row.fulfilmentStatus === 'Confirmed' ? 'success' : 'neutral'}>
                  {row.fulfilmentStatus}
                </Badge>
              </TableCell>
              <TableCell className="text-right tabular-nums">
                {formatMoney(row.grossAmountMinor, row.currency || currency)}
              </TableCell>
              {data.showsMargin ? (
                <TableCell className="text-right tabular-nums">
                  {row.marginMinor === null
                    ? '—'
                    : formatMoney(row.marginMinor, row.currency || currency)}
                </TableCell>
              ) : null}
            </TableRow>
          ))}
        </TableBody>
      </Table>

      <div className="flex items-center justify-between gap-3">
        <p className="text-sm text-muted-foreground">
          {data.total === 0
            ? 'No bookings in this window.'
            : `Showing ${first}–${last} of ${data.total}`}
        </p>

        {lastPage > 1 ? (
          <div className="flex items-center gap-2">
            <Button
              variant="outline"
              size="sm"
              disabled={data.page <= 1}
              onClick={() => onPage(data.page - 1)}
            >
              Previous
            </Button>
            <span className="text-sm text-muted-foreground">
              Page {data.page} of {lastPage}
            </span>
            <Button
              variant="outline"
              size="sm"
              disabled={data.page >= lastPage}
              onClick={() => onPage(data.page + 1)}
            >
              Next
            </Button>
          </div>
        ) : null}
      </div>
    </div>
  );
}
