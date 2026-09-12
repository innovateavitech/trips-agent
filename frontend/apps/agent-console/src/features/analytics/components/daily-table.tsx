import { formatMoney } from '@trips/utils';
import { Button, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@trips/ui';
import { formatDay } from '../analytics-rules';
import type { AnalyticsDay } from '../types';

/**
 * The daily series, as numbers rather than as a picture.
 *
 * Every day is a link into the drill-down, which is the point of the criterion:
 * an aggregate is only useful if you can get from it to the bookings behind it.
 */
export function DailyTable({
  days,
  currency,
  showsMargin,
  withYear,
  onSelectDay,
}: {
  days: AnalyticsDay[];
  currency: string;
  showsMargin: boolean;
  withYear: boolean;
  onSelectDay: (day: string) => void;
}) {
  return (
    <Table>
      <TableHeader>
        <TableRow>
          <TableHead>Day</TableHead>
          <TableHead className="text-right">Bookings</TableHead>
          <TableHead className="text-right">Gross sales</TableHead>
          {showsMargin ? <TableHead className="text-right">Margin</TableHead> : null}
          <TableHead className="text-right">Refunds</TableHead>
          <TableHead className="text-right">Unfulfilled</TableHead>
          <TableHead className="w-24" />
        </TableRow>
      </TableHeader>

      <TableBody>
        {[...days].reverse().map((day) => (
          <TableRow key={day.day}>
            <TableCell className="font-medium text-foreground">
              {formatDay(day.day, withYear)}
            </TableCell>
            <TableCell className="text-right tabular-nums">{day.bookings}</TableCell>
            <TableCell className="text-right tabular-nums">
              {formatMoney(day.grossSalesMinor, currency)}
            </TableCell>
            {showsMargin ? (
              <TableCell className="text-right tabular-nums">
                {day.marginMinor === null ? '—' : formatMoney(day.marginMinor, currency)}
              </TableCell>
            ) : null}
            <TableCell className="text-right tabular-nums">{day.refunds}</TableCell>
            <TableCell className="text-right tabular-nums">{day.failures}</TableCell>
            <TableCell className="text-right">
              <Button
                variant="ghost"
                size="sm"
                onClick={() => onSelectDay(day.day)}
                aria-label={`See the bookings behind ${formatDay(day.day, withYear)}`}
              >
                Bookings
              </Button>
            </TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}
