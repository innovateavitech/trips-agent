import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { Card, Skeleton } from '@trips/ui';
import { formatMoney } from '../../../lib/format';
import type { OperationsDashboard, SalesWindow } from '../types';

/**
 * The counts, as tiles.
 *
 * Ordered by what somebody should do about them, not by what is easiest to count: the two numbers
 * that mean a person has to act — bookings needing resolution, KYB waiting — come first, then the
 * shape of the platform.
 */
export function StatTiles({ dashboard }: { dashboard: OperationsDashboard }) {
  return (
    <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
      <Tile
        label="Bookings needing resolution"
        value={dashboard.bookingsNeedingResolution}
        tone={dashboard.bookingsNeedingResolution > 0 ? 'urgent' : 'calm'}
        detail="Paid for, and the supplier did not deliver."
      />
      <Tile
        label="KYB waiting"
        value={dashboard.pendingKybCount}
        tone={dashboard.pendingKybCount > 0 ? 'attention' : 'calm'}
        detail="Agencies that cannot sell until somebody looks."
        href="/kyb"
      />
      <Tile
        label="Open alerts"
        value={dashboard.openAlertCount}
        tone={dashboard.criticalAlertCount > 0 ? 'urgent' : 'calm'}
        detail={
          dashboard.criticalAlertCount > 0
            ? `${dashboard.criticalAlertCount} critical`
            : 'Nothing critical.'
        }
      />
      <Tile
        label="Agencies selling"
        value={dashboard.agencies.verified}
        tone="calm"
        detail={`${dashboard.agencies.total} on the platform · ${dashboard.agencies.suspended} suspended`}
        href="/agencies?status=Verified"
      />
    </div>
  );
}

/** What sold, over three windows. Money is always shown from minor units, never recomputed. */
export function SalesTiles({ sales }: { sales: SalesWindow[] }) {
  return (
    <div className="grid gap-3 sm:grid-cols-3">
      {sales.map((window) => (
        <Card key={window.label} className="flex flex-col gap-1 p-4">
          <p className="text-xs uppercase tracking-wide text-muted-foreground">{window.label}</p>
          <p className="text-2xl font-semibold tabular-nums text-foreground">
            {formatMoney(window.grossMinor, window.currency)}
          </p>
          <p className="text-sm text-muted-foreground">
            {window.orderCount} {window.orderCount === 1 ? 'order' : 'orders'} ·{' '}
            {formatMoney(window.platformFeeMinor, window.currency)} platform fee
          </p>
        </Card>
      ))}
    </div>
  );
}

function Tile({
  label,
  value,
  detail,
  tone,
  href,
}: {
  label: string;
  value: number;
  detail: string;
  /** Urgent gets the destructive token, attention the warning one, calm nothing at all. */
  tone: 'urgent' | 'attention' | 'calm';
  href?: string;
}) {
  const numberClass =
    tone === 'urgent'
      ? 'text-destructive'
      : tone === 'attention'
        ? 'text-warning-subtle-foreground'
        : 'text-foreground';

  const body: ReactNode = (
    <>
      <p className="text-xs uppercase tracking-wide text-muted-foreground">{label}</p>
      <p className={`text-3xl font-semibold tabular-nums ${numberClass}`}>{value}</p>
      <p className="text-sm text-muted-foreground">{detail}</p>
    </>
  );

  if (href) {
    return (
      <Card className="p-0">
        <Link
          to={href}
          className="flex flex-col gap-1 rounded-lg p-4 transition-colors hover:bg-accent"
        >
          {body}
        </Link>
      </Card>
    );
  }

  return <Card className="flex flex-col gap-1 p-4">{body}</Card>;
}

/** The same footprint as the tiles, so the page does not jump when the counts arrive. */
export function DashboardSkeleton() {
  return (
    <div aria-busy="true" aria-label="Counting" className="flex flex-col gap-4">
      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        {Array.from({ length: 4 }, (_, index) => (
          <Skeleton key={index} className="h-28 w-full" />
        ))}
      </div>
      <div className="grid gap-3 sm:grid-cols-3">
        {Array.from({ length: 3 }, (_, index) => (
          <Skeleton key={index} className="h-24 w-full" />
        ))}
      </div>
      <Skeleton className="h-64 w-full" />
    </div>
  );
}
