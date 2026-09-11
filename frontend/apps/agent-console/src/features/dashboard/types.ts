/**
 * What the dashboard reads. Hand-written until the orders API (#41, #42)
 * exists; then these come from `@trips/api-client` and this file is deleted.
 *
 * Every `*Minor` field is an integer number of kobo. ₦142,500.00 is 14250000.
 */

export type BookingStatus =
  /** Fare confirmed and paid for; the ticket has not been issued yet. */
  | 'awaiting_ticket'
  /** Held with the operator, not yet paid. Bus seats sit here briefly. */
  | 'confirmed'
  | 'ticketed'
  /** The supplier did not confirm. Needs the agent's decision (#44). */
  | 'failed'
  | 'cancelled';

export type ProductKind = 'flight' | 'bus';

export interface BookingSummary {
  /** Our order reference, e.g. `TRP-8K2Q4F`. */
  reference: string;
  leadTraveller: string;
  travellerCount: number;
  product: ProductKind;
  /** IATA codes for flights, city names for buses. */
  origin: string;
  destination: string;
  /** "Air Peace P7 7121", "GIG Mobility". */
  carrier: string;
  /** ISO 8601, UTC. */
  departsAt: string;
  status: BookingStatus;
  /** What the traveller pays: net + your markup + tax, frozen at booking. */
  sellMinor: number;
  currency: string;
  /**
   * The airline's deadline to issue, ISO 8601, when there is one. Miss it and
   * the booking dies — so the dashboard puts these first.
   */
  ticketTimeLimit: string | null;
}

export interface DashboardOverview {
  /** Bookings that need action, most urgent first. */
  needsAttention: BookingSummary[];
  /** The latest bookings, newest first. */
  recentBookings: BookingSummary[];
}
