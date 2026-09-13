/**
 * Group departures as the console sees them (build plan F6): a dated run of a
 * tour or package, sold by the seat, with a deposit and an installment plan.
 *
 * Field for field the departures contract in docs/BUILD_PLAN.md, so the
 * stand-in in `mock/` and the HTTP adapter that replaces it agree.
 */

export type DepartureStatus =
  'Open' | 'Guaranteed' | 'NearlyFull' | 'SoldOut' | 'Closed' | 'Cancelled';
export type DepositType = 'None' | 'Percent' | 'Fixed';
export type DueBasis = 'FromBooking' | 'BeforeDeparture';
export type DepartureAction = 'close' | 'reopen' | 'cancel';

/** The price per traveller for a party of this size: "4 to 7 travellers, ₦1,390,000 each". */
export interface PriceTier {
  minPax: number;
  /** Null: this size and up. */
  maxPax: number | null;
  pricePerPaxMinor: number;
}

/** One payment of the balance left after the deposit. */
export interface InstallmentItem {
  sequence: number;
  dueBasis: DueBasis;
  dueOffsetDays: number;
  /** A share of the balance in basis points: 5000 is half. All of them add up to 10,000. */
  percentOfBalanceBasisPoints: number;
}

export interface DepartureRequest {
  /** `YYYY-MM-DD`, the day it leaves. */
  departureDate: string;
  /** A group departure only runs once `minPax` travellers have booked. */
  isGroupDeparture: boolean;
  minPax: number;
  capacityTotal: number;
  /** Bookings close this many days before departure. */
  cutoffDaysBefore: number;
  depositType: DepositType;
  depositPercentBasisPoints: number | null;
  depositAmountMinor: number | null;
  priceTiers: PriceTier[];
  installments: InstallmentItem[];
}

export interface Departure extends DepartureRequest {
  id: string;
  productId: string;
  productTitle: string;
  currency: string;
  /** Worked out by the server from the seats, except Closed and Cancelled, which are the agent's. */
  status: DepartureStatus;
  /** Held during checkout, not yet paid. */
  capacityReserved: number;
  /** Paid for. */
  capacityConfirmed: number;
  seatsLeft: number;
  waitlistCount: number;
  /** The instant bookings close. */
  cutoffAt: string;
  /** Sent back with a save; a stale one is refused, so two people cannot overwrite each other. */
  version: number;
}

export interface ManifestEntry {
  orderReference: string;
  travellerName: string;
  paxType: 'Adult' | 'Child' | 'Infant';
  room: string | null;
  status: 'Reserved' | 'Confirmed';
}

export interface WaitlistEntry {
  id: string;
  name: string;
  paxCount: number;
  status: 'Waiting' | 'Offered' | 'Converted' | 'Expired';
  joinedAt: string;
  offeredAt: string | null;
  expiresAt: string | null;
}
