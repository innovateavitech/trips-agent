import { formatMoneyShort } from '@trips/utils';
import type {
  BookingDetail,
  BookingFilters,
  BookingListItem,
  BookingStatus,
  ResolutionAction,
} from './types';

/**
 * Everything the bookings screens decide, as plain functions — tested without
 * rendering anything in `__tests__/bookings-rules.test.ts`.
 */

/** The status filter, "Needs decision" first: it is money at risk, and must not be buried. */
export const STATUS_FILTERS: ReadonlyArray<{ value: BookingStatus | 'all'; label: string }> = [
  { value: 'all', label: 'All' },
  { value: 'failed', label: 'Needs decision' },
  { value: 'awaiting_ticket', label: 'Awaiting ticket' },
  { value: 'confirmed', label: 'Confirmed' },
  { value: 'ticketed', label: 'Ticketed' },
  { value: 'cancelled', label: 'Cancelled' },
];

export function filterBookings(
  bookings: readonly BookingListItem[],
  filters: BookingFilters,
): BookingListItem[] {
  const wanted = filters.query.trim().toLowerCase();

  return bookings.filter((booking) => {
    if (filters.status !== 'all' && booking.status !== filters.status) return false;
    if (filters.product !== 'all' && booking.product !== filters.product) return false;
    if (!wanted) return true;

    return [
      booking.reference,
      booking.pnr ?? '',
      booking.leadTraveller,
      booking.origin,
      booking.destination,
      booking.carrier,
    ]
      .join(' ')
      .toLowerCase()
      .includes(wanted);
  });
}

export function statusCounts(
  bookings: readonly BookingListItem[],
): Record<BookingStatus | 'all', number> {
  const counts: Record<BookingStatus | 'all', number> = {
    all: bookings.length,
    failed: 0,
    awaiting_ticket: 0,
    confirmed: 0,
    ticketed: 0,
    cancelled: 0,
  };
  for (const booking of bookings) counts[booking.status] += 1;
  return counts;
}

/** Newest first — what an agent looking for "the one I just did" expects. */
export function newestFirst(bookings: readonly BookingListItem[]): BookingListItem[] {
  return [...bookings].sort((a, b) => b.bookedAt.localeCompare(a.bookedAt));
}

export interface ResolutionCopy {
  title: string;
  body: string;
  confirm: string;
  /** Shown large before the agent confirms. Null when nothing more moves. */
  amountMinor: number | null;
}

/**
 * The words on a resolution's confirmation. The amount comes first for a
 * refund, because the agent is about to move a customer's money.
 */
export function resolutionCopy(
  action: Exclude<ResolutionAction, 'substitute'>,
  booking: BookingDetail,
): ResolutionCopy {
  const amount = booking.failure?.atRiskMinor ?? booking.sellMinor;

  if (action === 'retry') {
    return {
      title: `Ask ${booking.carrier} to issue again?`,
      body: 'The same fare, for the same travellers. Nothing more is charged. If the supplier refuses again, the booking comes back to this queue.',
      confirm: 'Try again',
      amountMinor: null,
    };
  }

  const toWallet = (booking.failure?.paidFrom ?? booking.paidFrom) === 'wallet';

  return {
    title: `Refund ${formatMoneyShort(amount, booking.currency)}?`,
    body: toWallet
      ? 'The full amount goes back to your wallet straight away, and the booking is cancelled.'
      : "The full amount goes back to the customer's card, and the booking is cancelled. Card refunds can take several working days to reach them.",
    confirm: `Refund ${formatMoneyShort(amount, booking.currency)}`,
    amountMinor: amount,
  };
}
