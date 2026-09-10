import { Badge, Card, CardContent, CardHeader, CardTitle, Skeleton } from '@trips/ui';
import { formatMoney } from '@trips/utils';
import type { WalletSummary } from '../types';

/**
 * The three numbers an agent actually needs, in the order they need them.
 *
 * "Available" is the headline rather than "total", because available is the
 * number that answers the only question they are asking: can I book this now?
 * A wallet showing a large total with almost all of it reserved would otherwise
 * be read as "yes" right up until checkout refuses.
 */
export function BalanceCard({ summary }: { summary: WalletSummary }) {
  return (
    <Card>
      <CardHeader className="flex-row items-start justify-between gap-4">
        <div className="flex flex-col gap-1">
          <CardTitle>Available to spend</CardTitle>
          <p className="text-3xl font-semibold tabular-nums tracking-tight text-foreground">
            {formatMoney(summary.availableMinor, summary.currency)}
          </p>
        </div>
        {summary.status === 'frozen' ? <Badge tone="destructive">Frozen</Badge> : null}
      </CardHeader>

      <CardContent className="grid gap-4 sm:grid-cols-2">
        <Figure
          label="Total balance"
          value={formatMoney(summary.balanceMinor, summary.currency)}
          help="Everything in the wallet, including funds already set aside."
        />
        <Figure
          label="Reserved"
          value={formatMoney(summary.reservedMinor, summary.currency)}
          help="Held against bookings that are confirmed but not yet ticketed."
        />
      </CardContent>
    </Card>
  );
}

function Figure({ label, value, help }: { label: string; value: string; help: string }) {
  return (
    <div className="flex flex-col gap-0.5">
      <span className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
        {label}
      </span>
      <span className="text-lg font-medium tabular-nums text-foreground">{value}</span>
      <span className="text-xs text-muted-foreground">{help}</span>
    </div>
  );
}

/** Same footprint as the real card, so the page does not jump when data lands. */
export function BalanceCardSkeleton() {
  return (
    <Card aria-busy="true" aria-label="Loading your wallet balance">
      <CardHeader className="flex flex-col gap-2">
        <Skeleton className="h-4 w-32" />
        <Skeleton className="h-9 w-56" />
      </CardHeader>
      <CardContent className="grid gap-4 sm:grid-cols-2">
        <div className="flex flex-col gap-1.5">
          <Skeleton className="h-3 w-24" />
          <Skeleton className="h-6 w-32" />
        </div>
        <div className="flex flex-col gap-1.5">
          <Skeleton className="h-3 w-20" />
          <Skeleton className="h-6 w-28" />
        </div>
      </CardContent>
    </Card>
  );
}
