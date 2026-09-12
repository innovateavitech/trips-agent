import { Badge } from '@trips/ui';
import { STATUS_LABEL, STATUS_TONE, describeSeats } from '../departure-rules';
import type { Departure, DepartureStatus } from '../types';

export function DepartureStatusBadge({ status }: { status: DepartureStatus }) {
  return <Badge tone={STATUS_TONE[status]}>{STATUS_LABEL[status]}</Badge>;
}

/**
 * Seats at a glance: paid in the brand colour, held in checkout lighter, free
 * in grey. The numbers are in the label, so a screen reader hears the same.
 */
export function SeatsBar({
  departure,
}: {
  departure: Pick<Departure, 'capacityTotal' | 'capacityConfirmed' | 'capacityReserved'>;
}) {
  const total = Math.max(1, departure.capacityTotal);
  const confirmed = Math.min(100, (departure.capacityConfirmed / total) * 100);
  const reserved = Math.min(100 - confirmed, (departure.capacityReserved / total) * 100);

  return (
    <div
      role="img"
      aria-label={describeSeats(departure)}
      className="flex h-2 w-full overflow-hidden rounded-full bg-muted"
    >
      <div className="h-full bg-primary" style={{ width: `${confirmed}%` }} />
      <div className="h-full bg-primary-subtle" style={{ width: `${reserved}%` }} />
    </div>
  );
}
