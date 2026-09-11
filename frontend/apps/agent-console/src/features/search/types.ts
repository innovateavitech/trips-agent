/**
 * ============================================================================
 *  The shapes the search screens work with.
 * ============================================================================
 *
 * Hand-written for now. The supplier search endpoints (#33, #34) are being
 * built in parallel and are not in the generated client yet — and the generated
 * client must never be hand-edited. When they land, these become re-exports of
 * the generated types and `mock/` is deleted. See `search-api.ts`.
 *
 * Money is in minor units (kobo), always — CLAUDE.md rule 2.
 */

export type TripType = 'one_way' | 'round_trip' | 'multi_city';

export type CabinClass = 'economy' | 'premium_economy' | 'business' | 'first';

export interface Airport {
  /** IATA code, upper case: `LOS`. */
  code: string;
  city: string;
  name: string;
  /** ISO 3166 alpha-2: `NG`. */
  country: string;
}

export interface Passengers {
  /** 12 and over. */
  adults: number;
  /** 2 to 11. */
  children: number;
  /** Under 2, travelling on an adult's lap — so never more of them than adults. */
  infants: number;
}

export interface FlightLeg {
  origin: string;
  destination: string;
  /** Local date, `YYYY-MM-DD`, exactly as `<input type="date">` gives it. */
  date: string;
}

export interface FlightSearchCriteria {
  tripType: TripType;
  /** One leg for one-way, two for a return (the second reversed), two to five for multi-city. */
  legs: FlightLeg[];
  passengers: Passengers;
  cabin: CabinClass;
}

/**
 * What a price is made of.
 *
 * `margin` is null unless the viewer holds `margin.view`. The API leaves it out
 * for everyone else, and `redactFlightResult` strips it again on this side —
 * the net rate is what Trips charges the agency, and a counter agent has no
 * business seeing their employer's cost price.
 */
export interface OfferPrice {
  currency: string;
  /** What the traveller pays, for the whole party. Always shown. */
  sellMinor: number;
  margin: {
    /** What Trips charges the agency, taxes included. */
    netMinor: number;
    /** What the agency adds on top. */
    markupMinor: number;
  } | null;
}

export interface FlightSegment {
  carrierCode: string;
  carrierName: string;
  flightNumber: string;
  origin: string;
  destination: string;
  /** Wall-clock time at the airport, `YYYY-MM-DDTHH:mm` — what is printed on the ticket. */
  departsAt: string;
  arrivesAt: string;
  durationMinutes: number;
}

export interface FlightJourney {
  segments: FlightSegment[];
  /** Door to door, connections included. */
  durationMinutes: number;
  stops: number;
}

/** The fare rules, in plain words — shown BEFORE the agent selects (FRD §2.3). */
export interface FareTerms {
  fareFamily: string;
  refundable: boolean;
  cancellation: string;
  changes: string;
  checkedBaggage: string;
  cabinBaggage: string;
}

export interface FlightOffer {
  id: string;
  /** One journey per leg searched: outbound then return, or each multi-city leg in turn. */
  journeys: FlightJourney[];
  cabin: CabinClass;
  /** Seats left at this fare, when the airline says. Null when it does not. */
  seatsLeft: number | null;
  terms: FareTerms;
  price: OfferPrice;
}

export interface BusTerminal {
  id: string;
  city: string;
  name: string;
}

export interface BusSearchCriteria {
  tripType: 'one_way' | 'round_trip';
  departureTerminalId: string;
  arrivalTerminalId: string;
  date: string;
  /** Required for a return, null for one-way. */
  returnDate: string | null;
  passengers: number;
}

export interface BusOffer {
  id: string;
  operator: string;
  /** "Toyota Hiace · 14 seats" — agents are asked this more than anything else. */
  vehicle: string;
  departureTerminal: BusTerminal;
  arrivalTerminal: BusTerminal;
  departsAt: string;
  arrivesAt: string;
  durationMinutes: number;
  availableSeats: number;
  amenities: string[];
  terms: { cancellation: string; luggage: string };
  price: OfferPrice;
}

export interface SearchResult<TOffer> {
  offers: TOffer[];
  /** An instant, ISO 8601. */
  searchedAt: string;
  /** After this the supplier will not honour these fares, and the screen has to say so. */
  expiresAt: string;
}

export interface BusSearchResult extends SearchResult<BusOffer> {
  /** The return departures for a round trip; null for one-way. */
  returnOffers: BusOffer[] | null;
}
