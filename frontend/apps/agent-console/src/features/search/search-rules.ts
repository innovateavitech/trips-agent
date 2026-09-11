import type {
  Airport,
  BusOffer,
  BusSearchCriteria,
  BusSearchResult,
  BusTerminal,
  CabinClass,
  FlightJourney,
  FlightOffer,
  FlightSearchCriteria,
  OfferPrice,
  Passengers,
  SearchResult,
} from './types';

/**
 * ============================================================================
 *  Everything the search screens decide, as plain functions.
 * ============================================================================
 *
 * No React in here, so every rule is testable without rendering anything —
 * `__tests__/search-rules.test.ts` covers them. The components only arrange
 * what these return.
 */

/** Airline systems book up to nine seated passengers in one reservation. */
export const MAX_SEATED_PASSENGERS = 9;
export const MAX_MULTI_CITY_LEGS = 5;
export const MAX_BUS_PASSENGERS = 10;

export const CABIN_LABELS: Record<CabinClass, string> = {
  economy: 'Economy',
  premium_economy: 'Premium economy',
  business: 'Business',
  first: 'First',
};

// ------------------------------------------------------------------ who sees the margin

/**
 * The roles that hold `margin.view`, mirroring the API's seeded roles
 * (IdentitySeedData): Owners and Managers see what the agency pays; Agents do not.
 */
const MARGIN_ROLES: ReadonlySet<string> = new Set(['Owner', 'Manager']);

/**
 * Whether this person may see the net rate and the margin.
 *
 * This only decides what the screen OFFERS to show. The API is what actually
 * withholds the figures — it reads the permission from the token, which the
 * browser cannot forge. When the session carries permissions rather than
 * roles, this becomes `permissions.includes('margin.view')`.
 */
export function canViewMargin(roles: readonly string[]): boolean {
  return roles.some((role) => MARGIN_ROLES.has(role));
}

function redactPrice<T extends { price: OfferPrice }>(offer: T): T {
  return offer.price.margin === null ? offer : { ...offer, price: { ...offer.price, margin: null } };
}

/**
 * The result with every margin removed, unless the viewer may see it.
 *
 * Runs in the query's `select`, so a component never even receives a net rate
 * it should not show — a gate in the data rather than in the markup, which a
 * future component could forget to apply.
 */
export function redactFlightResult(
  result: SearchResult<FlightOffer>,
  canView: boolean,
): SearchResult<FlightOffer> {
  return canView ? result : { ...result, offers: result.offers.map(redactPrice) };
}

export function redactBusResult(result: BusSearchResult, canView: boolean): BusSearchResult {
  if (canView) return result;
  return {
    ...result,
    offers: result.offers.map(redactPrice),
    returnOffers: result.returnOffers ? result.returnOffers.map(redactPrice) : null,
  };
}

// ------------------------------------------------------------------------------ dates

/** Today in Lagos as `YYYY-MM-DD` — the agency's day, not the browser's or UTC's. */
export function todayIn(timeZone = 'Africa/Lagos', now: Date = new Date()): string {
  // en-CA writes dates as YYYY-MM-DD, which is also what <input type="date"> wants.
  return new Intl.DateTimeFormat('en-CA', {
    timeZone,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
  }).format(now);
}

/** `addDays('2026-12-30', 3)` → `'2027-01-02'`. */
export function addDays(date: string, days: number): string {
  const [year, month, day] = date.split('-').map(Number);
  return new Date(Date.UTC(year ?? 1970, (month ?? 1) - 1, (day ?? 1) + days))
    .toISOString()
    .slice(0, 10);
}

function dayNumber(localDateTime: string): number {
  const [year, month, day] = localDateTime.slice(0, 10).split('-').map(Number);
  return Date.UTC(year ?? 1970, (month ?? 1) - 1, day ?? 1) / 86_400_000;
}

/** How many days after departure a flight lands: the `+1` next to an overnight arrival. */
export function dayOffset(departsAt: string, arrivesAt: string): number {
  return dayNumber(arrivesAt) - dayNumber(departsAt);
}

// ------------------------------------------------------------------------- validation

/** Field key → what is wrong with it, in words the agent can act on. Empty when valid. */
export type Problems = Record<string, string>;

export function legField(index: number, field: 'origin' | 'destination' | 'date'): string {
  return `leg-${index}-${field}`;
}

export function validateFlightCriteria(criteria: FlightSearchCriteria, today: string): Problems {
  const problems: Problems = {};

  criteria.legs.forEach((leg, index) => {
    // A return's airports are the outbound's reversed, so only its date is the agent's to get wrong.
    const isReturn = criteria.tripType === 'round_trip' && index === 1;

    if (!isReturn) {
      if (!leg.origin) problems[legField(index, 'origin')] = 'Choose where you are flying from.';
      if (!leg.destination) {
        problems[legField(index, 'destination')] = 'Choose where you are flying to.';
      } else if (leg.origin === leg.destination) {
        problems[legField(index, 'destination')] = 'Pick a destination other than the departure airport.';
      }
    }

    const previous = criteria.legs[index - 1];
    if (!leg.date) {
      problems[legField(index, 'date')] = isReturn ? 'Choose a return date.' : 'Choose a date.';
    } else if (leg.date < today) {
      problems[legField(index, 'date')] = 'That date has already passed.';
    } else if (previous?.date && leg.date < previous.date) {
      problems[legField(index, 'date')] = isReturn
        ? 'The return cannot be before the outbound flight.'
        : 'Each flight has to be on or after the one before it.';
    }
  });

  const problem = passengerProblem(criteria.passengers);
  if (problem) problems['passengers'] = problem;

  return problems;
}

function passengerProblem({ adults, children, infants }: Passengers): string | null {
  if (adults < 1) return 'At least one adult has to travel.';
  if (adults + children > MAX_SEATED_PASSENGERS) {
    return `One booking holds up to ${MAX_SEATED_PASSENGERS} passengers with seats. Split a larger group into separate bookings.`;
  }
  if (infants > adults) {
    return "Each infant travels on an adult's lap, so there cannot be more infants than adults.";
  }
  return null;
}

export function validateBusCriteria(
  criteria: BusSearchCriteria,
  today: string,
  terminals: readonly BusTerminal[],
): Problems {
  const problems: Problems = {};
  const from = terminals.find((terminal) => terminal.id === criteria.departureTerminalId);
  const to = terminals.find((terminal) => terminal.id === criteria.arrivalTerminalId);

  if (!from) problems['from'] = 'Choose where the bus leaves from.';
  if (!to) problems['to'] = 'Choose where the bus is going.';
  else if (from && from.city === to.city) problems['to'] = `Choose a terminal outside ${from.city}.`;

  if (!criteria.date) problems['date'] = 'Choose a date.';
  else if (criteria.date < today) problems['date'] = 'That date has already passed.';

  if (criteria.tripType === 'round_trip') {
    if (!criteria.returnDate) problems['returnDate'] = 'Choose a return date.';
    else if (criteria.date && criteria.returnDate < criteria.date) {
      problems['returnDate'] = 'The return cannot be before the outbound trip.';
    }
  }

  if (criteria.passengers < 1 || criteria.passengers > MAX_BUS_PASSENGERS) {
    problems['passengers'] = `Book between 1 and ${MAX_BUS_PASSENGERS} passengers at a time.`;
  }

  return problems;
}

// ---------------------------------------------------------------------- filtering

export type StopsFilter = 'any' | 'nonstop' | 'one_stop';

export type TimeOfDay = 'early' | 'morning' | 'afternoon' | 'evening';

export const TIME_OF_DAY_LABELS: Record<TimeOfDay, string> = {
  early: 'Before 6am',
  morning: '6am – 12pm',
  afternoon: '12pm – 6pm',
  evening: 'After 6pm',
};

export interface FlightFilters {
  stops: StopsFilter;
  /** Null means no cap. */
  maxPriceMinor: number | null;
  /** Carrier codes to keep. Empty means every carrier. */
  carriers: readonly string[];
  /** Outbound departure windows to keep. Empty means any time. */
  departureTimes: readonly TimeOfDay[];
}

export const NO_FLIGHT_FILTERS: FlightFilters = {
  stops: 'any',
  maxPriceMinor: null,
  carriers: [],
  departureTimes: [],
};

export function hasActiveFilters(filters: FlightFilters): boolean {
  return (
    filters.stops !== 'any' ||
    filters.maxPriceMinor !== null ||
    filters.carriers.length > 0 ||
    filters.departureTimes.length > 0
  );
}

export function activeFilterCount(filters: FlightFilters): number {
  return (
    (filters.stops !== 'any' ? 1 : 0) +
    (filters.maxPriceMinor !== null ? 1 : 0) +
    filters.carriers.length +
    filters.departureTimes.length
  );
}

export function timeOfDay(localDateTime: string): TimeOfDay {
  const hour = Number(localDateTime.slice(11, 13));
  if (hour < 6) return 'early';
  if (hour < 12) return 'morning';
  if (hour < 18) return 'afternoon';
  return 'evening';
}

/** The most connections on any journey in the offer — a return with one stop each way is "1 stop". */
export function maxStops(offer: FlightOffer): number {
  return offer.journeys.reduce((most, journey) => Math.max(most, journey.stops), 0);
}

/** The airline selling the first flight, which is the one the traveller will name. */
export function primaryCarrier(offer: FlightOffer): { code: string; name: string } {
  const first = offer.journeys[0]?.segments[0];
  return { code: first?.carrierCode ?? '', name: first?.carrierName ?? '' };
}

export function outboundDeparture(offer: FlightOffer): string {
  return offer.journeys[0]?.segments[0]?.departsAt ?? '';
}

export function totalDuration(offer: FlightOffer): number {
  return offer.journeys.reduce((sum, journey) => sum + journey.durationMinutes, 0);
}

export function applyFlightFilters(offers: readonly FlightOffer[], filters: FlightFilters): FlightOffer[] {
  return offers.filter((offer) => {
    const stops = maxStops(offer);
    if (filters.stops === 'nonstop' && stops > 0) return false;
    if (filters.stops === 'one_stop' && stops > 1) return false;
    if (filters.maxPriceMinor !== null && offer.price.sellMinor > filters.maxPriceMinor) return false;
    if (filters.carriers.length > 0 && !filters.carriers.includes(primaryCarrier(offer).code)) {
      return false;
    }
    if (
      filters.departureTimes.length > 0 &&
      !filters.departureTimes.includes(timeOfDay(outboundDeparture(offer)))
    ) {
      return false;
    }
    return true;
  });
}

export function stopCounts(offers: readonly FlightOffer[]): Record<StopsFilter, number> {
  return {
    any: offers.length,
    nonstop: offers.filter((offer) => maxStops(offer) === 0).length,
    one_stop: offers.filter((offer) => maxStops(offer) <= 1).length,
  };
}

export function carrierCounts(
  offers: readonly FlightOffer[],
): Array<{ code: string; name: string; count: number }> {
  const counts = new Map<string, { code: string; name: string; count: number }>();
  for (const offer of offers) {
    const carrier = primaryCarrier(offer);
    const entry = counts.get(carrier.code) ?? { ...carrier, count: 0 };
    entry.count += 1;
    counts.set(carrier.code, entry);
  }
  return [...counts.values()].sort((a, b) => a.name.localeCompare(b.name));
}

export function priceRange(offers: readonly FlightOffer[]): { min: number; max: number } | null {
  if (offers.length === 0) return null;
  const prices = offers.map((offer) => offer.price.sellMinor);
  return { min: Math.min(...prices), max: Math.max(...prices) };
}

// ------------------------------------------------------------------------ sorting

export type FlightSort = 'cheapest' | 'fastest' | 'earliest' | 'latest';

export function sortFlights(offers: readonly FlightOffer[], sort: FlightSort): FlightOffer[] {
  const byPrice = (a: FlightOffer, b: FlightOffer) => a.price.sellMinor - b.price.sellMinor;
  const byDuration = (a: FlightOffer, b: FlightOffer) => totalDuration(a) - totalDuration(b);
  const byDeparture = (a: FlightOffer, b: FlightOffer) =>
    outboundDeparture(a).localeCompare(outboundDeparture(b));

  return [...offers].sort((a, b) => {
    switch (sort) {
      case 'cheapest':
        return byPrice(a, b) || byDuration(a, b);
      case 'fastest':
        return byDuration(a, b) || byPrice(a, b);
      case 'earliest':
        return byDeparture(a, b) || byPrice(a, b);
      case 'latest':
        return byDeparture(b, a) || byPrice(a, b);
    }
  });
}

export type BusSort = 'earliest' | 'cheapest' | 'fastest';

export function sortBuses(offers: readonly BusOffer[], sort: BusSort): BusOffer[] {
  return [...offers].sort((a, b) => {
    switch (sort) {
      case 'earliest':
        return a.departsAt.localeCompare(b.departsAt) || a.price.sellMinor - b.price.sellMinor;
      case 'cheapest':
        return a.price.sellMinor - b.price.sellMinor || a.departsAt.localeCompare(b.departsAt);
      case 'fastest':
        return a.durationMinutes - b.durationMinutes || a.price.sellMinor - b.price.sellMinor;
    }
  });
}

export function operatorCounts(offers: readonly BusOffer[]): Array<{ name: string; count: number }> {
  const counts = new Map<string, number>();
  for (const offer of offers) counts.set(offer.operator, (counts.get(offer.operator) ?? 0) + 1);
  return [...counts.entries()]
    .map(([name, count]) => ({ name, count }))
    .sort((a, b) => a.name.localeCompare(b.name));
}

// ------------------------------------------------------------------------- airports

/**
 * Airports matching what the agent typed, best first: an exact code, then a
 * city that starts with it, then a code, then a word of the airport's name.
 * "los" → Lagos; "lag" → Lagos; "murtala" → Lagos; "lon" → both London airports.
 */
export function matchAirports(query: string, airports: readonly Airport[], limit = 8): Airport[] {
  const wanted = query.trim().toLowerCase();
  if (!wanted) return airports.slice(0, limit);

  const scored: Array<{ airport: Airport; score: number }> = [];
  for (const airport of airports) {
    const code = airport.code.toLowerCase();
    const city = airport.city.toLowerCase();
    const name = airport.name.toLowerCase();

    let score = -1;
    if (code === wanted) score = 0;
    else if (city.startsWith(wanted)) score = 1;
    else if (code.startsWith(wanted)) score = 2;
    else if (name.split(/\s+/).some((word) => word.startsWith(wanted))) score = 3;
    else if (city.includes(wanted) || name.includes(wanted)) score = 4;

    if (score >= 0) scored.push({ airport, score });
  }

  // Array.prototype.sort is stable, so equal scores keep the list's busiest-first order.
  return scored
    .sort((a, b) => a.score - b.score)
    .slice(0, limit)
    .map((entry) => entry.airport);
}

// ------------------------------------------------------------------------ formatting

/** `formatDuration(65)` → `'1h 05m'`; `formatDuration(45)` → `'45m'`. */
export function formatDuration(minutes: number): string {
  const hours = Math.floor(minutes / 60);
  const rest = minutes % 60;
  return hours === 0 ? `${rest}m` : `${hours}h ${String(rest).padStart(2, '0')}m`;
}

/** `'2026-09-14T07:30'` → `'07:30'`. Local airport time, never converted. */
export function formatClock(localDateTime: string): string {
  return localDateTime.slice(11, 16);
}

/** `'2026-09-14'` → `'Mon, 14 Sept'`. */
export function formatDay(date: string): string {
  return new Intl.DateTimeFormat('en-NG', {
    weekday: 'short',
    day: 'numeric',
    month: 'short',
    timeZone: 'UTC',
  }).format(new Date(`${date.slice(0, 10)}T00:00:00Z`));
}

export function stopsLabel(journey: FlightJourney): string {
  if (journey.stops === 0) return 'Direct';
  const via = journey.segments
    .slice(0, -1)
    .map((segment) => segment.destination)
    .join(', ');
  return journey.stops === 1 ? `1 stop · ${via}` : `${journey.stops} stops · ${via}`;
}

function plural(count: number, one: string, many: string): string {
  return `${count} ${count === 1 ? one : many}`;
}

/** `'2 adults, 1 child, 1 infant'`. */
export function describePassengers({ adults, children, infants }: Passengers): string {
  return [
    plural(adults, 'adult', 'adults'),
    children > 0 ? plural(children, 'child', 'children') : null,
    infants > 0 ? plural(infants, 'infant', 'infants') : null,
  ]
    .filter(Boolean)
    .join(', ');
}

export function describeBusPassengers(count: number): string {
  return plural(count, 'passenger', 'passengers');
}

// ---------------------------------------------------------------------------- expiry

export function secondsUntil(expiresAt: string, nowMs: number): number {
  return Math.max(0, Math.floor((Date.parse(expiresAt) - nowMs) / 1000));
}

/** `formatCountdown(545)` → `'9:05'`. */
export function formatCountdown(seconds: number): string {
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
}
