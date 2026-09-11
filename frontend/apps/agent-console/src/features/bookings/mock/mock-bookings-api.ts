import { formatMoneyShort } from '@trips/utils';
import { ApiError } from '../../../api/errors';
import type { BookingsApi } from '../bookings-api';
import { newestFirst } from '../bookings-rules';
import type { BookingDetail } from '../types';
import { allBookings, findBooking, scheduleSettlement, updateBooking } from './booking-store';

/**
 * ============================================================================
 *  TEMPORARY. Delete this folder when the orders API lands (#42, #44).
 * ============================================================================
 *
 * The bookings screens' stand-in, over the shared in-memory store. A retried
 * booking tickets itself a few seconds later, so the detail page can be seen
 * moving from "Awaiting ticket" to "Ticketed". A refund does not really move
 * the stand-in wallet's balance — the two stand-ins keep separate books.
 */

const LATENCY_MS = 300;
const RETRY_TICKETS_AFTER_MS = 8_000;

const delay = (ms = LATENCY_MS) => new Promise((resolve) => setTimeout(resolve, ms));

/** A copy, so a screen can never change the store by holding on to what it was given. */
const copy = (booking: BookingDetail): BookingDetail => structuredClone(booking);

export const mockBookingsApi: BookingsApi = {
  async listBookings() {
    await delay();
    return newestFirst(allBookings().map(copy));
  },

  async getBooking(reference) {
    await delay();
    const booking = findBooking(reference);
    if (!booking) {
      throw new ApiError(
        404,
        'We could not find that booking.',
        'It may belong to another agency, or the reference may be mistyped.',
      );
    }
    return copy(booking);
  },

  async resolve(reference, action) {
    await delay(600);
    const booking = findBooking(reference);

    if (!booking) throw new ApiError(404, 'We could not find that booking.');
    if (booking.status !== 'failed') {
      throw new ApiError(
        409,
        'That booking no longer needs a decision.',
        'Someone may have resolved it already.',
      );
    }

    const now = new Date().toISOString();
    const amount = booking.failure?.atRiskMinor ?? booking.sellMinor;

    const resolved = updateBooking(reference, (current) =>
      action === 'retry'
        ? {
            ...current,
            status: 'awaiting_ticket',
            failure: null,
            ticketTimeLimit: new Date(Date.now() + 2 * 60 * 60_000).toISOString(),
            timeline: [
              ...current.timeline,
              {
                at: now,
                status: 'awaiting_ticket',
                note: `You asked ${current.carrier} to issue again`,
              },
            ],
          }
        : {
            ...current,
            status: 'cancelled',
            failure: null,
            ticketTimeLimit: null,
            timeline: [
              ...current.timeline,
              {
                at: now,
                status: 'cancelled',
                note: `Refunded ${formatMoneyShort(amount, current.currency)} to ${
                  current.paidFrom === 'wallet' ? 'your wallet' : "the customer's card"
                }`,
              },
            ],
          },
    );

    if (action === 'retry') scheduleSettlement(reference, 'ticketed', RETRY_TICKETS_AFTER_MS);

    return copy(resolved!);
  },
};
