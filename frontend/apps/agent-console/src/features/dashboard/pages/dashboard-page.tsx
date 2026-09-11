import { Bus, CalendarClock, Plane } from 'lucide-react';
import { Link } from 'react-router-dom';
import {
  Badge,
  Card,
  CardDescription,
  CardHeader,
  CardTitle,
  EmptyState,
  ErrorState,
  Skeleton,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
  buttonVariants,
  cn,
} from '@trips/ui';
import { formatMoney } from '@trips/utils';
import { describeError } from '../../../api/errors';
import { displayNameFor } from '../../../auth/auth-api';
import { useCurrentUser } from '../../../auth/auth-provider';
import { PageHeader } from '../../../shell/page-header';
import { useWalletSummary } from '../../wallet';
import { useDashboardOverview } from '../dashboard-api';
import {
  STATUS_DISPLAY,
  describeTimeLeft,
  formatDeparture,
  greetingFor,
  type Urgency,
} from '../booking-display';
import type { BookingSummary } from '../types';

/**
 * Where an agent lands: what needs doing before a deadline passes, how much
 * they can spend, and what was booked lately. In that order, because a missed
 * ticket time limit is the one thing on this page that cannot be undone.
 */
export function DashboardPage() {
  const user = useCurrentUser();
  const overview = useDashboardOverview();
  const now = new Date();

  return (
    <>
      <PageHeader
        title={`${greetingFor(now)}, ${displayNameFor(user).split(' ')[0]}`}
        description={
          user.agency
            ? `Here is what needs you today at ${user.agency.name}.`
            : 'Here is what needs you today.'
        }
        actions={
          <>
            <Link to="/search/buses" className={buttonVariants({ variant: 'outline', size: 'sm' })}>
              <Bus aria-hidden="true" className="h-4 w-4" />
              Search buses
            </Link>
            <Link to="/search/flights" className={buttonVariants({ size: 'sm' })}>
              <Plane aria-hidden="true" className="h-4 w-4" />
              Search flights
            </Link>
          </>
        }
      />

      <div className="grid gap-6 lg:grid-cols-3">
        <Card className="lg:col-span-2">
          <CardHeader>
            <CardTitle>Needs your attention</CardTitle>
            <CardDescription>
              Issue tickets before the airline&rsquo;s time limit, or the fare is lost.
            </CardDescription>
          </CardHeader>
          <div className="border-t border-border">
            {overview.isPending ? <AttentionSkeleton /> : null}
            {overview.isError ? (
              <div className="p-5">
                <ErrorState
                  {...describeError(overview.error)}
                  onRetry={() => void overview.refetch()}
                  retrying={overview.isFetching}
                />
              </div>
            ) : null}
            {overview.data && overview.data.needsAttention.length === 0 ? (
              <EmptyState
                icon={<CalendarClock aria-hidden="true" className="h-5 w-5" />}
                title="Nothing is waiting on you"
              >
                Bookings close to their ticket time limit, and any the airline did not confirm,
                appear here first.
              </EmptyState>
            ) : null}
            {overview.data && overview.data.needsAttention.length > 0 ? (
              <ul className="divide-y divide-border">
                {overview.data.needsAttention.map((booking) => (
                  <AttentionRow key={booking.reference} booking={booking} now={now} />
                ))}
              </ul>
            ) : null}
          </div>
        </Card>

        <WalletGlance />
      </div>

      <Card className="overflow-hidden">
        <CardHeader className="flex-row items-center justify-between gap-4">
          <div className="flex flex-col gap-1">
            <CardTitle>Recent bookings</CardTitle>
            <CardDescription>The latest bookings across your agency.</CardDescription>
          </div>
          <Link to="/bookings" className={buttonVariants({ variant: 'ghost', size: 'sm' })}>
            View all
          </Link>
        </CardHeader>
        <RecentBookings query={overview} />
      </Card>
    </>
  );
}

const URGENCY_TONE: Record<Urgency, 'destructive' | 'warning' | 'neutral'> = {
  expired: 'destructive',
  urgent: 'destructive',
  soon: 'warning',
  comfortable: 'neutral',
};

function AttentionRow({ booking, now }: { booking: BookingSummary; now: Date }) {
  const failed = booking.status === 'failed';
  const timeLeft = booking.ticketTimeLimit ? describeTimeLeft(booking.ticketTimeLimit, now) : null;

  return (
    <li className="flex flex-wrap items-center gap-x-4 gap-y-2 px-5 py-4">
      <div className="flex min-w-0 flex-1 flex-col gap-0.5">
        <p className="text-sm font-medium text-foreground">
          <Route booking={booking} />
        </p>
        <p className="truncate text-sm text-muted-foreground">
          {booking.leadTraveller}
          {booking.travellerCount > 1 ? ` and ${booking.travellerCount - 1} more` : ''},{' '}
          {booking.carrier}
        </p>
      </div>
      {failed ? (
        <Badge tone="destructive">Airline did not confirm</Badge>
      ) : timeLeft ? (
        <Badge tone={URGENCY_TONE[timeLeft.urgency]} className="tabular-nums">
          {timeLeft.label}
        </Badge>
      ) : null}
      <Link
        to={failed ? '/resolution' : `/bookings/${booking.reference}`}
        className={buttonVariants({ variant: failed ? 'outline' : 'primary', size: 'sm' })}
      >
        {failed ? 'Resolve' : 'Issue ticket'}
      </Link>
    </li>
  );
}

function Route({ booking }: { booking: BookingSummary }) {
  const Icon = booking.product === 'flight' ? Plane : Bus;
  return (
    <span className="inline-flex items-center gap-2">
      <Icon aria-hidden="true" className="h-4 w-4 text-muted-foreground" />
      {booking.origin}
      <span className="sr-only">to</span>
      <span aria-hidden="true" className="text-muted-foreground">
        →
      </span>
      {booking.destination}
    </span>
  );
}

function AttentionSkeleton() {
  return (
    <div aria-busy="true" aria-label="Loading bookings that need attention">
      {[0, 1, 2].map((row) => (
        <div
          key={row}
          className="flex items-center gap-4 border-b border-border px-5 py-4 last:border-b-0"
        >
          <div className="flex flex-1 flex-col gap-2">
            <Skeleton className="h-4 w-32" />
            <Skeleton className="h-3 w-56" />
          </div>
          <Skeleton className="h-8 w-24" />
        </div>
      ))}
    </div>
  );
}

function WalletGlance() {
  const summary = useWalletSummary();

  return (
    <Card className="flex flex-col">
      <CardHeader>
        <CardTitle>Wallet</CardTitle>
        <CardDescription>What you can spend on bookings right now.</CardDescription>
      </CardHeader>
      <div className="flex flex-1 flex-col justify-between gap-5 px-5 pb-5">
        {summary.isPending ? (
          <div className="flex flex-col gap-2" aria-busy="true" aria-label="Loading your balance">
            <Skeleton className="h-8 w-40" />
            <Skeleton className="h-4 w-32" />
          </div>
        ) : null}
        {summary.isError ? (
          <ErrorState
            title="We could not load your balance"
            detail="Your money is safe; this is a problem reading it."
            onRetry={() => void summary.refetch()}
          />
        ) : null}
        {summary.data ? (
          <dl className="flex flex-col gap-3">
            <div>
              <dt className="text-sm text-muted-foreground">Available</dt>
              <dd className="text-3xl font-semibold tracking-tight text-foreground tabular-nums">
                {formatMoney(summary.data.availableMinor, summary.data.currency)}
              </dd>
            </div>
            <div className="flex justify-between text-sm">
              <dt className="text-muted-foreground">Held for bookings</dt>
              <dd className="font-medium tabular-nums text-foreground">
                {formatMoney(summary.data.reservedMinor, summary.data.currency)}
              </dd>
            </div>
          </dl>
        ) : null}
        <Link to="/wallet" className={buttonVariants({ variant: 'outline', fullWidth: true })}>
          Top up or view statement
        </Link>
      </div>
    </Card>
  );
}

function RecentBookings({ query }: { query: ReturnType<typeof useDashboardOverview> }) {
  if (query.isPending) {
    return (
      <div
        className="flex flex-col gap-3 px-5 pb-5"
        aria-busy="true"
        aria-label="Loading recent bookings"
      >
        {[0, 1, 2, 3].map((row) => (
          <Skeleton key={row} className="h-9 w-full" />
        ))}
      </div>
    );
  }

  // The attention card above already shows the error and the retry button.
  if (query.isError) return null;

  if (query.data.recentBookings.length === 0) {
    return (
      <EmptyState
        title="No bookings yet"
        action={
          <Link to="/search/flights" className={buttonVariants({ size: 'sm' })}>
            Search flights
          </Link>
        }
      >
        When you book a flight or a bus, it appears here with its status.
      </EmptyState>
    );
  }

  return (
    <div className="overflow-x-auto border-t border-border">
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>Reference</TableHead>
            <TableHead>Trip</TableHead>
            <TableHead className="hidden md:table-cell">Departs</TableHead>
            <TableHead className="text-right">Sell price</TableHead>
            <TableHead>Status</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {query.data.recentBookings.map((booking) => {
            const status = STATUS_DISPLAY[booking.status];
            return (
              <TableRow key={booking.reference}>
                <TableCell>
                  <Link
                    to={`/bookings/${booking.reference}`}
                    className="font-medium text-primary underline-offset-4 hover:underline"
                  >
                    {booking.reference}
                  </Link>
                  <p className="text-xs text-muted-foreground">{booking.leadTraveller}</p>
                </TableCell>
                <TableCell>
                  <Route booking={booking} />
                  <p className="text-xs text-muted-foreground">{booking.carrier}</p>
                </TableCell>
                <TableCell className="hidden whitespace-nowrap tabular-nums md:table-cell">
                  {formatDeparture(booking.departsAt)}
                </TableCell>
                <TableCell className={cn('text-right font-medium tabular-nums')}>
                  {formatMoney(booking.sellMinor, booking.currency)}
                </TableCell>
                <TableCell>
                  <Badge tone={status.tone}>{status.label}</Badge>
                </TableCell>
              </TableRow>
            );
          })}
        </TableBody>
      </Table>
    </div>
  );
}
