import type { BusOffer, FlightOffer, Passengers } from '../search/types';

/**
 * ============================================================================
 *  What the booking flow works with (#53).
 * ============================================================================
 *
 * Hand-written until the checkout saga (#42) exists. Every `*Minor` field is an
 * integer number of kobo.
 */

/** The fare the agent chose, carried in from the search results. */
export type BookingDraft =
  | { product: 'flight'; offer: FlightOffer; passengers: Passengers }
  | { product: 'bus'; offer: BusOffer; passengers: Passengers };

/** Airline passenger types: adult 12+, child 2–11, infant under 2 on a lap. */
export type TravellerType = 'ADT' | 'CHD' | 'INF';

export interface TravellerDetails {
  type: TravellerType;
  title: string;
  firstName: string;
  lastName: string;
  /** `YYYY-MM-DD`. Required for children and infants: the fare depends on age. */
  dateOfBirth: string;
  gender: '' | 'female' | 'male';
  /** The lead traveller only: where the supplier sends schedule changes. */
  email: string;
  phone: string;
  /** Only when the route leaves Nigeria. */
  passportNumber: string;
  passportExpiry: string;
  /** Two letters: NG. */
  nationality: string;
}

/** What the supplier confirmed once the travellers were known. */
export interface PriceConfirmation {
  /**
   * The server's booking this confirmation created, which paying for it names. Absent only from the
   * demo stand-in, which keeps its own books.
   */
  reference?: string;
  /** What the customer pays now. */
  sellMinor: number;
  /** What the search showed. Differs only when the supplier moved the price. */
  searchedSellMinor: number;
  currency: string;
  /** ISO 8601, UTC: issue before this, or the supplier releases the fare. */
  ticketTimeLimit: string;
}

export type PaymentMethod = 'wallet' | 'card';

export interface PlaceBookingInput {
  draft: BookingDraft;
  travellers: TravellerDetails[];
  payment: PaymentMethod;
  confirmation: PriceConfirmation;
  /**
   * One per attempt, reused when the agent retries after an error — so a second
   * press, or a retry after a lost answer, is the same booking, never a second
   * charge. The server keeps its own guard as well.
   */
  idempotencyKey: string;
}

export interface PlacedBooking {
  reference: string;
}

/** Where a placed booking has got to. Only "ticketed" is a finished success. */
export interface BookingProgress {
  status: 'awaiting_ticket' | 'ticketed' | 'failed';
  pnr: string | null;
}
