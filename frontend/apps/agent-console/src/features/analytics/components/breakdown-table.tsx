import { formatMoney } from '@trips/utils';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@trips/ui';
import type { AnalyticsBreakdown } from '../types';

/** How a window split by product type or by channel. */
export function BreakdownTable({
  caption,
  rows,
  currency,
  showsMargin,
}: {
  caption: string;
  rows: AnalyticsBreakdown[];
  currency: string;
  showsMargin: boolean;
}) {
  if (rows.length === 0) {
    return (
      <div className="flex flex-col gap-2">
        <h3 className="text-sm font-semibold text-foreground">{caption}</h3>
        <p className="text-sm text-muted-foreground">Nothing sold in this window.</p>
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-2">
      <h3 className="text-sm font-semibold text-foreground">{caption}</h3>

      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>{caption}</TableHead>
            <TableHead className="text-right">Bookings</TableHead>
            <TableHead className="text-right">Gross</TableHead>
            {showsMargin ? <TableHead className="text-right">Margin</TableHead> : null}
          </TableRow>
        </TableHeader>

        <TableBody>
          {rows.map((row) => (
            <TableRow key={row.label}>
              <TableCell className="font-medium text-foreground">{row.label}</TableCell>
              <TableCell className="text-right tabular-nums">{row.bookings}</TableCell>
              <TableCell className="text-right tabular-nums">
                {formatMoney(row.grossSalesMinor, currency)}
              </TableCell>
              {showsMargin ? (
                <TableCell className="text-right tabular-nums">
                  {row.marginMinor === null ? '—' : formatMoney(row.marginMinor, currency)}
                </TableCell>
              ) : null}
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </div>
  );
}
