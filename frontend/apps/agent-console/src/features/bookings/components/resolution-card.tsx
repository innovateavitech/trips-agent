import { Bus, Plane } from 'lucide-react';
import { useState } from 'react';
import { Link } from 'react-router-dom';
import {
  Badge,
  Button,
  Card,
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogTitle,
} from '@trips/ui';
import { formatMoneyShort } from '@trips/utils';
import { formatDeparture } from '../../dashboard/booking-display';
import { resolutionCopy } from '../bookings-rules';
import type { BookingDetail, ResolutionAction } from '../types';

type Decision = Exclude<ResolutionAction, 'substitute'>;

export interface ResolutionCardProps {
  booking: BookingDetail;
  /** Which action is on its way to the server, if any. */
  resolving: Decision | null;
  onResolve: (action: Decision) => void;
  onSubstitute: () => void;
}

/**
 * One failed booking in the resolution queue: why it failed, the money at risk
 * — first and large, because it is a customer's money — and the three things
 * the agent can do. Retrying and refunding are confirmed in a dialog that shows
 * the amount before anything moves.
 */
export function ResolutionCard({
  booking,
  resolving,
  onResolve,
  onSubstitute,
}: ResolutionCardProps) {
  const [asking, setAsking] = useState<Decision | null>(null);
  const failure = booking.failure;
  if (!failure) return null;

  const copy = asking ? resolutionCopy(asking, booking) : null;
  const Icon = booking.product === 'flight' ? Plane : Bus;
  const amount = formatMoneyShort(failure.atRiskMinor, booking.currency);

  return (
    <Card className="overflow-hidden border-destructive">
      <div className="flex flex-col gap-4 p-5 sm:flex-row sm:items-start sm:justify-between">
        <div className="flex min-w-0 flex-col gap-1">
          <div className="flex flex-wrap items-center gap-2">
            <Badge tone="destructive">Needs decision</Badge>
            <Link
              to={`/bookings/${booking.reference}`}
              className="text-sm font-medium text-primary underline-offset-4 hover:underline"
            >
              {booking.reference}
            </Link>
          </div>
          <p className="flex items-center gap-2 text-base font-semibold text-foreground">
            <Icon aria-hidden="true" className="h-4 w-4 text-muted-foreground" />
            {booking.origin}
            <span className="sr-only">to</span>
            <span aria-hidden="true" className="text-muted-foreground">
              →
            </span>
            {booking.destination}
          </p>
          <p className="text-sm text-muted-foreground">
            {booking.leadTraveller}
            {booking.travellerCount > 1 ? ` and ${booking.travellerCount - 1} more` : ''} ·{' '}
            {booking.carrier} · {formatDeparture(booking.departsAt)}
          </p>
          <p className="mt-2 text-sm text-foreground">{failure.reason}</p>
        </div>

        <div className="shrink-0 sm:text-right">
          <p className="text-xs text-muted-foreground">At risk</p>
          <p className="text-2xl font-semibold tabular-nums text-destructive">{amount}</p>
          <p className="text-xs text-muted-foreground">
            Paid {failure.paidFrom === 'wallet' ? 'from your wallet' : "on the customer's card"}
          </p>
        </div>
      </div>

      <div className="flex flex-wrap gap-2 border-t border-border bg-muted px-5 py-3">
        <Button
          variant="outline"
          size="sm"
          loading={resolving === 'retry'}
          disabled={resolving !== null}
          onClick={() => setAsking('retry')}
        >
          Try again
        </Button>
        <Button variant="outline" size="sm" disabled={resolving !== null} onClick={onSubstitute}>
          Find another fare
        </Button>
        <Button
          variant="destructive"
          size="sm"
          loading={resolving === 'refund'}
          disabled={resolving !== null}
          onClick={() => setAsking('refund')}
        >
          Refund {amount}
        </Button>
      </div>

      <Dialog open={asking !== null} onOpenChange={(open) => (open ? undefined : setAsking(null))}>
        {copy && asking ? (
          <DialogContent>
            <DialogTitle>{copy.title}</DialogTitle>
            <DialogDescription>{copy.body}</DialogDescription>
            {copy.amountMinor !== null ? (
              <p className="text-3xl font-semibold tabular-nums text-foreground">
                {formatMoneyShort(copy.amountMinor, booking.currency)}
              </p>
            ) : null}
            <DialogFooter>
              <Button variant="outline" onClick={() => setAsking(null)}>
                Not now
              </Button>
              <Button
                variant={asking === 'refund' ? 'destructive' : 'primary'}
                onClick={() => {
                  onResolve(asking);
                  setAsking(null);
                }}
              >
                {copy.confirm}
              </Button>
            </DialogFooter>
          </DialogContent>
        ) : null}
      </Dialog>
    </Card>
  );
}
