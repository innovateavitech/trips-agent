import { Card, Skeleton } from '@trips/ui';
import { formatMoney } from '../../../lib/format';
import { changeTone, formatBasisPoints } from '../analytics-rules';
import type { PlatformAnalytics } from '../types';

/**
 * The four numbers that describe the platform.
 *
 * GMV and Trips' revenue are separate tiles, labelled so they cannot be confused: GMV is what
 * travellers paid across every agency, and the platform's revenue is the fee taken out of agents'
 * margins (MVP decision 4). Showing one and calling it the other is the single easiest way to
 * mislead a board.
 */
export function PlatformTiles({ data }: { data: PlatformAnalytics }) {
  const tone = changeTone(data.gmvChangeBasisPoints);

  return (
    <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
      <Tile
        label="GMV"
        value={formatMoney(data.gmvMinor, data.currency)}
        detail={
          data.gmvChangeBasisPoints === null ? (
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
              {formatBasisPoints(data.gmvChangeBasisPoints, true)} on{' '}
              {formatMoney(data.previousGmvMinor, data.currency)}
            </span>
          )
        }
      />

      <Tile
        label="Trips revenue"
        value={formatMoney(data.platformFeeMinor, data.currency)}
        detail={
          <span className="text-muted-foreground">
            The fee taken from agents&rsquo; margins, not from travellers
          </span>
        }
      />

      <Tile
        label="Agencies selling"
        value={data.activeAgencies.toLocaleString('en-NG')}
        detail={
          <span className="text-muted-foreground">{data.newAgencies} signed up in this window</span>
        }
      />

      <Tile
        label="Bookings"
        value={data.bookings.toLocaleString('en-NG')}
        detail={
          <span className="text-muted-foreground">
            {data.failures} unfulfilled · {data.refunds} refunded
          </span>
        }
      />
    </div>
  );
}

function Tile({ label, value, detail }: { label: string; value: string; detail: React.ReactNode }) {
  return (
    <Card className="flex flex-col gap-1 p-4">
      <p className="text-xs uppercase tracking-wide text-muted-foreground">{label}</p>
      <p className="text-2xl font-semibold tabular-nums text-foreground">{value}</p>
      <p className="text-xs">{detail}</p>
    </Card>
  );
}

export function PlatformTilesSkeleton() {
  return (
    <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
      {[0, 1, 2, 3].map((index) => (
        <Card key={index} className="flex flex-col gap-2 p-4">
          <Skeleton className="h-3 w-20" />
          <Skeleton className="h-7 w-32" />
          <Skeleton className="h-3 w-40" />
        </Card>
      ))}
    </div>
  );
}
