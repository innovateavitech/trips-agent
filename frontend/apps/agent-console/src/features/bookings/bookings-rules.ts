import { formatMoneyShort } from '@trips/utils';
import { AGENCY_TIME_ZONE } from '../dashboard/booking-display';
import type {
  BookingDetail,
  BookingFilters,
  BookingListItem,
  BookingStatus,
  ResolutionAction,
} from './types';

const lagosDayFormat = new Intl.DateTimeFormat('en-CA', {
  timeZone: AGENCY_TIME_ZONE,
  year: 'numeric',
  month: '2-digit',
  day: '2-digit',
});

/**
 * The calendar day an instant falls on in Lagos, as `YYYY-MM-DD` — the day an
 * agent means when they pick a date. A flight at 23:30 UTC on the 1st leaves on
 * the 2nd in Lagos, and that is the day it has to be found under.
 */
export function lagosDay(iso: string): string {
  return lagosDayFormat.format(new Date(iso));
}

/** Words for a date range that cannot match anything, or `null` when it is fine. */
export function dateRangeProblem(filters: Pick<BookingFilters, 'from' | 'to'>): string | null {
  return filters.from && filters.to && filters.to < filters.from
    ? 'Choose a "to" date on or after the "from" date.'
    : null;
}

function withinDates(booking: BookingListItem, filters: BookingFilters): boolean {
  if (!filters.from && !filters.to) return true;

  // `YYYY-MM-DD` strings compare correctly as text, and both ends are inclusive.
  const day = lagosDay(filters.dateField === 'booked' ? booking.bookedAt : booking.departsAt);
  if (filters.from && day < filters.from) return false;
  if (filters.to && day > filters.to) return false;
  return true;
}

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
    if (!withinDates(booking, filters)) return false;
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
