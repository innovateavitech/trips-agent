import { Bus, Plane } from 'lucide-react';
import { Card } from '@trips/ui';
import { formatMoneyShort } from '@trips/utils';
import { cx } from '../../search/class-names';
import {
  describePassengers,
  formatClock,
  formatCountdown,
  formatDay,
} from '../../search/search-rules';
import type { BusOffer, FlightJourney } from '../../search/types';
import { useCountdown } from '../../search/use-countdown';
import type { BookingDraft, PriceConfirmation } from '../types';

/**
 * The trip, beside every step of the flow: where, when, how much — and once the
 * supplier has confirmed, the deadline to issue by, counting down.
 */
export function TripSummary({
  draft,
  confirmation,
}: {
  draft: BookingDraft;
  confirmation: PriceConfirmation | null;
}) {
  const sellMinor = confirmation?.sellMinor ?? draft.offer.price.sellMinor;
  const currency = draft.offer.price.currency;

  return (
    <Card className="flex flex-col gap-4 p-5">
      <h2 className="text-sm font-semibold text-foreground">Your trip</h2>

      <ul className="flex flex-col gap-3">
        {draft.product === 'flight' ? (
          draft.offer.journeys.map((journey, index) => (
            <JourneyLine key={index} journey={journey} />
          ))
        ) : (
          <BusLine offer={draft.offer} />
        )}
      </ul>

      <p className="text-sm text-muted-foreground">{describePassengers(draft.passengers)}</p>

      <div className="flex flex-col gap-1 border-t border-border pt-4">
        <p className="text-xs text-muted-foreground">Total</p>
        <p className="text-2xl font-semibold tabular-nums text-foreground">
          {formatMoneyShort(sellMinor, currency)}
        </p>
        <p
          className={cx(
            'text-xs',
            confirmation ? 'text-success-subtle-foreground' : 'text-muted-foreground',
          )}
        >
          {confirmation
            ? 'Confirmed by the supplier'
            : 'From the search — confirmed once the travellers are entered'}
        </p>
      </div>

      {confirmation ? <TimeLimit deadline={confirmation.ticketTimeLimit} /> : null}
    </Card>
  );
}

function JourneyLine({ journey }: { journey: FlightJourney }) {
  const first = journey.segments[0];
  const last = journey.segments[journey.segments.length - 1];
  if (!first || !last) return null;

  return (
    <li className="flex gap-3">
      <Plane aria-hidden="true" className="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" />
      <div className="min-w-0">
        <p className="text-sm font-medium text-foreground">
          {first.origin} → {last.destination}
        </p>
        <p className="text-xs text-muted-foreground">
          {formatDay(first.departsAt)} · {formatClock(first.departsAt)}–
          {formatClock(last.arrivesAt)} · {first.carrierName}
        </p>
      </div>
    </li>
  );
}

function BusLine({ offer }: { offer: BusOffer }) {
  return (
    <li className="flex gap-3">
      <Bus aria-hidden="true" className="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" />
      <div className="min-w-0">
        <p className="text-sm font-medium text-foreground">
          {offer.departureTerminal.city} → {offer.arrivalTerminal.city}
        </p>
        <p className="text-xs text-muted-foreground">
          {formatDay(offer.departsAt)} · {formatClock(offer.departsAt)} · {offer.operator},{' '}
          {offer.departureTerminal.name}
        </p>
      </div>
    </li>
  );
}

/**
 * The supplier's ticket time limit, live (#53). Quiet with time to spare, amber
 * in the last ten minutes, red once it has passed — when nothing can be issued.
 */
function TimeLimit({ deadline }: { deadline: string }) {
  const secondsLeft = useCountdown(deadline);
  const urgent = secondsLeft <= 10 * 60;

  return (
    <p
      role="timer"
      className={cx(
        'rounded-md p-3 text-sm',
        secondsLeft === 0
          ? 'bg-destructive-subtle text-destructive-subtle-foreground'
          : urgent
            ? 'bg-warning-subtle text-warning-subtle-foreground'
            : 'bg-muted text-foreground',
      )}
    >
      {secondsLeft === 0 ? (
        'The time limit has passed. The supplier no longer holds this fare.'
      ) : (
        <>
          Issue within{' '}
          <span className="font-semibold tabular-nums">{formatCountdown(secondsLeft)}</span>, or the
          supplier releases the fare.
        </>
      )}
    </p>
  );
}
