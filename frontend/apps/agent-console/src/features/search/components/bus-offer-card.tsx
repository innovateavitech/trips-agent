import { Bus } from 'lucide-react';
import { Badge, Button, Card } from '@trips/ui';
import { dayOffset, formatClock, formatDuration } from '../search-rules';
import type { BusOffer } from '../types';
import { Endpoint, PriceBlock, Timeline } from './offer-parts';

export interface BusOfferCardProps {
  offer: BusOffer;
  passengers: number;
  priceCaption: string;
  expired: boolean;
  onSelect: (offer: BusOffer) => void;
}

/**
 * One departure. Seats left is on the face of the card because a 14-seat
 * Hiace with three seats left cannot take a family of four — and the agent
 * should find that out here, not at the till.
 */
export function BusOfferCard({
  offer,
  passengers,
  priceCaption,
  expired,
  onSelect,
}: BusOfferCardProps) {
  const soldOut = offer.availableSeats === 0;
  const tooFew = !soldOut && offer.availableSeats < passengers;
  const unavailable = soldOut || tooFew;

  return (
    <Card className="overflow-hidden">
      <div className="flex flex-col gap-5 p-5 md:flex-row md:items-center">
        <div className="flex items-center gap-3 md:w-48 md:shrink-0">
          <span
            aria-hidden="true"
            className="flex h-9 w-9 shrink-0 items-center justify-center rounded-full bg-primary-subtle text-primary"
          >
            <Bus className="h-4 w-4" />
          </span>
          <div className="min-w-0">
            <p className="truncate text-sm font-medium text-foreground">{offer.operator}</p>
            <p className="truncate text-xs text-muted-foreground">{offer.vehicle}</p>
          </div>
        </div>

        <div className="flex min-w-0 flex-1 items-center gap-3">
          <Endpoint time={formatClock(offer.departsAt)} place={offer.departureTerminal.name} />
          <Timeline duration={formatDuration(offer.durationMinutes)} caption="Direct" />
          <Endpoint
            time={formatClock(offer.arrivesAt)}
            place={offer.arrivalTerminal.name}
            dayShift={dayOffset(offer.departsAt, offer.arrivesAt)}
            align="end"
          />
        </div>

        <div className="flex items-end justify-between gap-4 border-t border-border pt-4 md:w-48 md:flex-col md:items-end md:border-l md:border-t-0 md:pl-5 md:pt-0">
          <PriceBlock price={offer.price} caption={priceCaption} />
          <Button
            onClick={() => onSelect(offer)}
            disabled={expired || unavailable}
            className="md:w-full"
          >
            {soldOut ? 'Sold out' : tooFew ? 'Not enough seats' : 'Select'}
          </Button>
        </div>
      </div>

      <div className="flex flex-wrap items-center gap-2 border-t border-border px-5 py-3">
        {soldOut ? (
          <Badge tone="destructive">Sold out</Badge>
        ) : (
          <Badge tone={offer.availableSeats <= 5 ? 'warning' : 'neutral'}>
            {offer.availableSeats === 1 ? '1 seat' : `${offer.availableSeats} seats`} left
          </Badge>
        )}
        {offer.amenities.map((amenity) => (
          <Badge key={amenity} tone="neutral">
            {amenity}
          </Badge>
        ))}
        <p className="basis-full text-xs text-muted-foreground lg:ml-auto lg:basis-auto">
          {offer.terms.cancellation} {offer.terms.luggage}
        </p>
      </div>
    </Card>
  );
}
