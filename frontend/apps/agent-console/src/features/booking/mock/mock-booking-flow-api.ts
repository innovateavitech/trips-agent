import { ApiError } from '../../../api/errors';
import {
  addBooking,
  codeFrom,
  findBooking,
  scheduleSettlement,
} from '../../bookings/mock/booking-store';
import type { BookingDetail, TimelineEntry } from '../../bookings/types';
import type { BookingFlowApi } from '../booking-api';
import { carrierOf, confirmedMargin } from '../booking-rules';
import type { BookingDraft, PlaceBookingInput } from '../types';

/**
 * ============================================================================
 *  TEMPORARY. Delete this folder when the checkout saga lands (#42).
 * ============================================================================
 *
 * The booking flow's stand-in. A booking it places goes into the same store the
 * bookings screens read, so it appears in Bookings, and tickets itself about six
 * seconds later — long enough to see the honest "we're confirming" state.
 *
 * Two demo triggers, for the states that are otherwise hard to reach:
 *   Arik Air, or Libra Motors  the price goes up ₦3,500 at confirmation
 *   United Nigeria             the airline does not confirm; the booking lands
 *                              in the resolution queue
 *
 * Card payment is simulated: nothing opens, and it succeeds. The stand-in
 * wallet's balance does not really move — the two stand-ins keep separate books.
 */

const CONFIRM_LATENCY_MS = 900;
const PLACE_LATENCY_MS = 1_200;
const PROGRESS_LATENCY_MS = 300;
const ISSUE_AFTER_MS = 6_000;
const PRICE_RISE_MINOR = 350_000;
const FLIGHT_HOLD_MS = 45 * 60_000;
const BUS_HOLD_MS = 20 * 60_000;

const delay = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

/** Idempotency key → the booking it placed, so a second press is the same booking. */
const placed = new Map<string, string>();

function risesAtConfirmation(draft: BookingDraft): boolean {
  return draft.product === 'flight'
    ? draft.offer.journeys[0]?.segments[0]?.carrierName === 'Arik Air'
    : draft.offer.operator === 'Libra Motors';
}

function failsAtIssue(draft: BookingDraft): boolean {
  return draft.product === 'flight' && draft.offer.journeys[0]?.segments[0]?.carrierCode === 'UN';
}

/** A Lagos wall-clock time, `YYYY-MM-DDTHH:mm`, as an instant. */
const lagosInstant = (wallClock: string) => new Date(`${wallClock}:00+01:00`).toISOString();

function detailFrom(order: PlaceBookingInput, reference: string): BookingDetail {
  const { draft, travellers, payment, confirmation } = order;
  const now = new Date().toISOString();
  const lead = travellers[0];

  const timeline: TimelineEntry[] = [
    {
      at: now,
      status: 'awaiting_ticket',
      note:
        payment === 'wallet'
          ? 'Booked and paid from your wallet'
          : "Booked, paid on the customer's card",
    },
  ];

  const common = {
    reference,
    leadTraveller: lead ? `${lead.firstName} ${lead.lastName}` : '',
    travellerCount: travellers.length,
    status: 'awaiting_ticket' as const,
    sellMinor: confirmation.sellMinor,
    currency: confirmation.currency,
    ticketTimeLimit: confirmation.ticketTimeLimit,
    pnr: null,
    bookedAt: now,
    paidFrom: payment,
    travellers: travellers.map((traveller) => ({
      type: traveller.type,
      name: `${traveller.firstName} ${traveller.lastName}`,
      ticketNumber: null,
    })),
    price: {
      sellMinor: confirmation.sellMinor,
      margin: confirmedMargin(draft.offer.price.margin, confirmation),
    },
    timeline,
    failure: null,
  };

  if (draft.product === 'flight') {
    const segments = draft.offer.journeys.flatMap((journey) => journey.segments);
    const outbound = draft.offer.journeys[0]?.segments ?? [];
    const first = segments[0];
    const lastOutbound = outbound[outbound.length - 1];

    return {
      ...common,
      product: 'flight',
      origin: first?.origin ?? '',
      destination: lastOutbound?.destination ?? '',
      carrier: first ? `${first.carrierName} ${first.flightNumber}` : '',
      departsAt: first ? lagosInstant(first.departsAt) : now,
      segments: segments.map((segment) => ({
        carrier: `${segment.carrierName} ${segment.flightNumber}`,
        origin: segment.origin,
        destination: segment.destination,
        departsAt: segment.departsAt,
        arrivesAt: segment.arrivesAt,
      })),
    };
  }

  const bus = draft.offer;
  return {
    ...common,
    product: 'bus',
    origin: bus.departureTerminal.city,
    destination: bus.arrivalTerminal.city,
    carrier: bus.operator,
    departsAt: lagosInstant(bus.departsAt),
    segments: [
      {
        carrier: `${bus.operator} · ${bus.vehicle}`,
        origin: `${bus.departureTerminal.city} (${bus.departureTerminal.name})`,
        destination: `${bus.arrivalTerminal.city} (${bus.arrivalTerminal.name})`,
        departsAt: bus.departsAt,
        arrivesAt: bus.arrivesAt,
      },
    ],
  };
}

export const mockBookingFlowApi: BookingFlowApi = {
  async confirmPrice(draft) {
    await delay(CONFIRM_LATENCY_MS);
    const searched = draft.offer.price.sellMinor;

    return {
      sellMinor: searched + (risesAtConfirmation(draft) ? PRICE_RISE_MINOR : 0),
      searchedSellMinor: searched,
      currency: draft.offer.price.currency,
      ticketTimeLimit: new Date(
        Date.now() + (draft.product === 'flight' ? FLIGHT_HOLD_MS : BUS_HOLD_MS),
      ).toISOString(),
    };
  },

  async placeBooking(order) {
    await delay(PLACE_LATENCY_MS);

    // The same key again — a double press, or a retry after a lost answer — is the same booking.
    const existing = placed.get(order.idempotencyKey);
    if (existing) return { reference: existing };

    const reference = `TRP-${codeFrom(order.idempotencyKey)}`;
    placed.set(order.idempotencyKey, reference);
    addBooking(detailFrom(order, reference));

    scheduleSettlement(
      reference,
      failsAtIssue(order.draft) ? 'failed' : 'ticketed',
      ISSUE_AFTER_MS,
      `${carrierOf(order.draft)} did not confirm the seats, so no ticket was issued.`,
    );

    return { reference };
  },

  async getProgress(reference) {
    await delay(PROGRESS_LATENCY_MS);
    const booking = findBooking(reference);

    if (!booking) throw new ApiError(404, 'We could not find that booking.');

    return {
      status:
        booking.status === 'ticketed'
          ? 'ticketed'
          : booking.status === 'failed'
            ? 'failed'
            : 'awaiting_ticket',
      pnr: booking.pnr,
    };
  },
};
