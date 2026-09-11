import { ChevronDown, Luggage } from 'lucide-react';
import { useId, useState, type ReactNode } from 'react';
import { Badge, Button, Card } from '@trips/ui';
import { cx } from '../class-names';
import {
  CABIN_LABELS,
  dayOffset,
  formatClock,
  formatDay,
  formatDuration,
  stopsLabel,
} from '../search-rules';
import type { FlightJourney, FlightOffer } from '../types';
import { Endpoint, PriceBlock, Timeline } from './offer-parts';

export interface FlightOfferCardProps {
  offer: FlightOffer;
  /** "3 passengers total" — the price is for the whole party, and the card has to say so. */
  priceCaption: string;
  /** Once the fares have expired, nothing can be selected: the price is no longer real. */
  expired: boolean;
  onSelect: (offer: FlightOffer) => void;
}

/**
 * One fare. The terms that change a traveller's mind — refundable or not, how
 * much baggage — sit on the face of the card, and the full fare rules are one
 * click away, BEFORE the Select button is pressed (FRD §2.3). An agent who
 * sells a non-refundable fare without saying so owns the argument afterwards.
 */
export function FlightOfferCard({ offer, priceCaption, expired, onSelect }: FlightOfferCardProps) {
  const [termsOpen, setTermsOpen] = useState(false);
  const termsId = useId();
  const { terms } = offer;
  const journeyCount = offer.journeys.length;

  return (
    <Card className="overflow-hidden">
      <div className="flex flex-col gap-5 p-5 lg:flex-row lg:items-center">
        <div className="flex min-w-0 flex-1 flex-col gap-5">
          {offer.journeys.map((journey, index) => (
            <JourneyRow
              key={index}
              journey={journey}
              label={journeyCount > 1 ? journeyLabel(index, journeyCount) : null}
            />
          ))}
        </div>

        <div className="flex items-end justify-between gap-4 border-t border-border pt-4 lg:w-52 lg:flex-col lg:items-end lg:border-l lg:border-t-0 lg:pl-5 lg:pt-0">
          <PriceBlock price={offer.price} caption={priceCaption} />
          <Button onClick={() => onSelect(offer)} disabled={expired} className="lg:w-full">
            Select
          </Button>
        </div>
      </div>

      <div className="flex flex-wrap items-center gap-2 border-t border-border px-5 py-3">
        <Badge tone={terms.refundable ? 'success' : 'neutral'}>
          {terms.refundable ? 'Refundable' : 'Non-refundable'}
        </Badge>
        <Badge tone="neutral">
          <Luggage className="h-3 w-3" aria-hidden="true" />
          {terms.checkedBaggage} checked
        </Badge>
        <span className="text-xs text-muted-foreground">
          {CABIN_LABELS[offer.cabin]} · {terms.fareFamily}
        </span>
        {offer.seatsLeft !== null && offer.seatsLeft <= 4 ? (
          <Badge tone="warning">
            {offer.seatsLeft === 1 ? '1 seat' : `${offer.seatsLeft} seats`} left at this fare
          </Badge>
        ) : null}
        <Button
          variant="link"
          size="sm"
          className="ml-auto px-0"
          aria-expanded={termsOpen}
          aria-controls={termsId}
          onClick={() => setTermsOpen((open) => !open)}
        >
          Fare rules
          <ChevronDown
            aria-hidden="true"
            className={cx(
              'h-4 w-4 transition-transform motion-reduce:transition-none',
              termsOpen && 'rotate-180',
            )}
          />
        </Button>
      </div>

      {/* `hidden` sits on a wrapper with no display class of its own: on the <dl> itself,
          Tailwind's `grid` would override it and every card would open with its rules showing. */}
      <div id={termsId} hidden={!termsOpen}>
        <dl className="grid gap-4 border-t border-border bg-muted px-5 py-4 text-sm sm:grid-cols-2">
          <Term label="Cancellation">{terms.cancellation}</Term>
          <Term label="Changes">{terms.changes}</Term>
          <Term label="Checked baggage">{terms.checkedBaggage}</Term>
          <Term label="Cabin baggage">{terms.cabinBaggage}</Term>
        </dl>
      </div>
    </Card>
  );
}

function journeyLabel(index: number, count: number): string {
  if (count === 2) return index === 0 ? 'Outbound' : 'Return';
  return `Flight ${index + 1}`;
}

function JourneyRow({ journey, label }: { journey: FlightJourney; label: string | null }) {
  const first = journey.segments[0];
  const last = journey.segments[journey.segments.length - 1];
  if (!first || !last) return null;

  return (
    <div className="flex flex-col gap-2">
      {label ? (
        <p className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
          {label} · {formatDay(first.departsAt)}
        </p>
      ) : null}

      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:gap-5">
        <div className="flex items-center gap-3 sm:w-40 sm:shrink-0">
          <span
            aria-hidden="true"
            className="flex h-9 w-9 shrink-0 items-center justify-center rounded-full bg-primary-subtle text-xs font-semibold text-primary"
          >
            {first.carrierCode}
          </span>
          <div className="min-w-0">
            <p className="truncate text-sm font-medium text-foreground">{first.carrierName}</p>
            <p className="truncate text-xs text-muted-foreground">
              {journey.segments.map((segment) => segment.flightNumber).join(' · ')}
            </p>
          </div>
        </div>

        <div className="flex min-w-0 flex-1 items-center gap-3">
          <Endpoint time={formatClock(first.departsAt)} place={first.origin} />
          <Timeline
            duration={formatDuration(journey.durationMinutes)}
            caption={stopsLabel(journey)}
            stops={journey.stops}
          />
          <Endpoint
            time={formatClock(last.arrivesAt)}
            place={last.destination}
            dayShift={dayOffset(first.departsAt, last.arrivesAt)}
            align="end"
          />
        </div>
      </div>
    </div>
  );
}

function Term({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div>
      <dt className="text-xs font-medium text-muted-foreground">{label}</dt>
      <dd className="mt-0.5 text-foreground">{children}</dd>
    </div>
  );
}
