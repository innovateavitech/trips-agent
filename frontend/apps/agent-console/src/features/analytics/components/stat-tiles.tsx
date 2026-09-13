import { formatMoney } from '@trips/utils';
import { Card, Skeleton } from '@trips/ui';
import { changeTone, formatChange } from '../analytics-rules';
import type { AnalyticsTotals } from '../types';

/**
 * The four numbers at the top of the page.
 *
 * Margin is here only when the account may see it. When it may not, the tile is
 * replaced by one that says so — an agent who cannot see margin should learn
 * that from the screen rather than wonder where the number went.
 */
export function StatTiles({
  totals,
  showsMargin,
}: {
  totals: AnalyticsTotals;
  showsMargin: boolean;
}) {
  const change = formatChange(totals.changeBasisPoints);
  const tone = changeTone(totals.changeBasisPoints);

  return (
    <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
      <Tile
        label="Gross sales"
        value={formatMoney(totals.grossSalesMinor, totals.currency)}
        footnote={
          change === null ? (
            <span className="text-muted-foreground">
              Nothing sold in the window before this one
            </span>
          ) : (
            <span
              className={
                tone === 'up'
                  ? 'text-success'
                  : tone === 'down'
                    ? 'text-destructive'
                    : 'text-muted-foreground'
              }
            >
              {change} on the previous{' '}
              {formatMoney(totals.previousGrossSalesMinor, totals.currency)}
            </span>
          )
        }
      />

      <Tile
        label="Bookings"
        value={totals.bookings.toLocaleString('en-NG')}
        footnote={
          <span className="text-muted-foreground">
            across {totals.orders.toLocaleString('en-NG')}{' '}
            {totals.orders === 1 ? 'order' : 'orders'}
          </span>
        }
      />

      {showsMargin && totals.marginMinor !== null ? (
        <Tile
          label="Your margin"
          value={formatMoney(totals.marginMinor, totals.currency)}
          footnote={
            <span className="text-muted-foreground">
              after {formatMoney(totals.netCostMinor ?? 0, totals.currency)} of cost
            </span>
          }
        />
      ) : (
        <Tile
          label="Your margin"
          value="—"
          footnote={
            <span className="text-muted-foreground">
              Only owners and managers can see what a booking cost
            </span>
          }
        />
      )}

      <Tile
        label="Needs attention"
        value={(totals.failures + totals.refunds).toLocaleString('en-NG')}
        footnote={
          <span className="text-muted-foreground">
            {totals.failures} unfulfilled, {totals.refunds} refunded
          </span>
        }
      />
    </div>
  );
}

function Tile({
  label,
  value,
  footnote,
}: {
  label: string;
  value: string;
  footnote: React.ReactNode;
}) {
  return (
    <Card className="flex flex-col gap-1 p-4">
      <span className="text-sm text-muted-foreground">{label}</span>
      <span className="text-2xl font-semibold tracking-tight text-foreground">{value}</span>
      <span className="text-xs">{footnote}</span>
    </Card>
  );
}

export function StatTilesSkeleton() {
  return (
    <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
      {[0, 1, 2, 3].map((index) => (
        <Card key={index} className="flex flex-col gap-2 p-4">
          <Skeleton className="h-4 w-24" />
          <Skeleton className="h-7 w-32" />
          <Skeleton className="h-3 w-40" />
        </Card>
      ))}
    </div>
  );
}
