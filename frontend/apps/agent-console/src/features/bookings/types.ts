import type { Schemas } from '@trips/api-client';
import type { BookingStatus, BookingSummary, ProductKind } from '../dashboard/types';

/**
 * ============================================================================
 *  What the bookings screens read (#54).
 * ============================================================================
 *
 * Hand-written until the orders API (#42, #44) exists; then these come from
 * `@trips/api-client` and this file goes. Built on the dashboard's own
 * `BookingSummary`, so the two screens share one vocabulary for a booking and
 * its status rather than drifting apart.
 *
 * Every `*Minor` field is an integer number of kobo.
 */

export type { BookingStatus, BookingSummary, ProductKind };

/** A row in the bookings list: the summary, plus what the list is searched and sorted by. */
export interface BookingListItem extends BookingSummary {
  /** The airline's or operator's reference, once there is one. */
  pnr: string | null;
  /** ISO 8601, UTC. */
  bookedAt: string;
}

export interface BookingTraveller {
  type: 'ADT' | 'CHD' | 'INF';
  name: string;
  /** The issued ticket number, once there is one. Buses have none. */
  ticketNumber: string | null;
}

export interface BookingSegment {
  /** "Air Peace P4 7121", or "GIG Mobility · Toyota Hiace". */
  carrier: string;
  origin: string;
  destination: string;
  /** Local wall-clock time, `YYYY-MM-DDTHH:mm` — what the ticket prints. */
  departsAt: string;
  arrivesAt: string | null;
}

export interface TimelineEntry {
  /** ISO 8601, UTC. */
  at: string;
  status: BookingStatus;
  /** What happened, in words: "Ticketed — PNR QX7K2P". */
  note: string;
}

/** Paid from the agency's wallet, or on the customer's card — which decides where a refund goes. */
export type PaidFrom = 'wallet' | 'card';

export interface BookingFailure {
  /** Why, as the supplier or our own checks said it. */
  reason: string;
  /** The money at risk: what the customer paid and has not yet got anything for. */
  atRiskMinor: number;
  paidFrom: PaidFrom;
}

export interface BookingDetail extends BookingListItem {
  paidFrom: PaidFrom;
  travellers: BookingTraveller[];
  segments: BookingSegment[];
  /**
   * `margin` is null unless the viewer holds `margin.view` — the API leaves it
   * out, and the query's `select` strips it again on this side.
   */
  price: { sellMinor: number; margin: { netMinor: number; markupMinor: number } | null };
  /** Oldest first. */
  timeline: TimelineEntry[];
  /** Set while the booking waits in the resolution queue. */
  failure: BookingFailure | null;
}

/**
 * What an agent can do about a failed booking (#54): ask again, find a different
 * fare, or give the money back. Substituting is a new search, not a call here.
 */
export type ResolutionAction = 'retry' | 'substitute' | 'refund';

/**
 * An invoice or voucher issued for a booking (#46), exactly as
 * `GET /api/v1/documents?orderReference=…` returns it. Taken from the generated
 * client rather than written out here, so the stand-in in `mock/` and the real
 * endpoint cannot disagree about its shape.
 */
export type BookingDocument = Schemas['BookingDocumentResponse'];

/** Which date the date filter reads: when the trip leaves, or when it was booked. */
export type BookingDateField = 'departure' | 'booked';

export interface BookingFilters {
  status: BookingStatus | 'all';
  product: ProductKind | 'all';
  /** Free text: a PNR, our reference, a traveller's name, a place. */
  query: string;
  /** Whether `from` and `to` are travel dates or booking dates. */
  dateField: BookingDateField;
  /** `YYYY-MM-DD`, a day in Lagos, inclusive — or `''` for no earliest day. */
  from: string;
  /** `YYYY-MM-DD`, a day in Lagos, inclusive — or `''` for no latest day. */
  to: string;
}

export const NO_BOOKING_FILTERS: BookingFilters = {
  status: 'all',
  product: 'all',
  query: '',
  dateField: 'departure',
  from: '',
  to: '',
};
