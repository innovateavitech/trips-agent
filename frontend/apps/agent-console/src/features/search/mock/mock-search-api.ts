import { ApiError } from '../../../api/errors';
import { findAirport, findTerminal } from '../reference-data';
import type { SearchApi } from '../search-api';
import type {
  Airport,
  BusOffer,
  BusTerminal,
  CabinClass,
  FareTerms,
  FlightJourney,
  FlightLeg,
  FlightOffer,
  FlightSegment,
  OfferPrice,
} from '../types';

/**
 * ============================================================================
 *  TEMPORARY. Delete this folder when the supplier search endpoints land (#33, #34).
 * ============================================================================
 *
 * Plausible Nigerian fares so the search screens can be seen doing their job.
 * The same search always shows the same fares, so a demo can be repeated. It
 * answers after ~1.5 seconds, because real supplier search takes seconds and
 * the loading state is part of what is being shown.
 *
 * Two demo triggers, for the states that are otherwise hard to reach:
 *   Kano → Sokoto    (KAN → SKO)  the airline system times out — "try again"
 *   Ibadan → Ilorin  (IBA → ILR)  nothing flies — the empty state
 *
 * Fares are held for ten minutes, then the screen marks them expired.
 */

const LATENCY_MS = 1_500;
const FARE_HOLD_MS = 10 * 60 * 1000;
const KOBO_PER_NAIRA = 100;

const TIMEOUT_ROUTE = { origin: 'KAN', destination: 'SKO' };
const NOTHING_FLIES_ROUTE = { origin: 'IBA', destination: 'ILR' };

type Random = () => number;

function wait(ms: number, signal?: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    const abort = () => reject(new DOMException('The search was cancelled.', 'AbortError'));
    if (signal?.aborted) {
      abort();
      return;
    }
    const timer = setTimeout(resolve, ms);
    signal?.addEventListener(
      'abort',
      () => {
        clearTimeout(timer);
        abort();
      },
      { once: true },
    );
  });
}

/** A small seeded generator (FNV-1a into mulberry32), so a search always shows the same fares. */
function seeded(seedText: string): Random {
  let hash = 2166136261;
  for (let i = 0; i < seedText.length; i++) {
    hash ^= seedText.charCodeAt(i);
    hash = Math.imul(hash, 16777619);
  }
  let state = hash >>> 0;
  return () => {
    state = (state + 0x6d2b79f5) >>> 0;
    let t = state;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

function between(random: Random, min: number, max: number): number {
  return Math.floor(min + random() * (max - min + 1));
}

function pick<T>(random: Random, items: readonly T[]): T {
  const item = items[Math.floor(random() * items.length)];
  if (item === undefined) throw new Error('pick() needs at least one item.');
  return item;
}

function roundTo(value: number, step: number): number {
  return Math.round(value / step) * step;
}

function pad(value: number): string {
  return String(value).padStart(2, '0');
}

/** Adds minutes to a local wall-clock time, treating it as timezone-free — which a ticket is. */
function addMinutes(localDateTime: string, minutes: number): string {
  const [date = '', time = ''] = localDateTime.split('T');
  const [year, month, day] = date.split('-').map(Number);
  const [hour, minute] = time.split(':').map(Number);
  const start = Date.UTC(year ?? 1970, (month ?? 1) - 1, day ?? 1, hour ?? 0, minute ?? 0);
  return new Date(start + minutes * 60_000).toISOString().slice(0, 16);
}

/** Whole naira in, an `OfferPrice` in kobo out. Integer arithmetic throughout. */
function price(netNaira: number, markupRate: number): OfferPrice {
  const netMinor = roundTo(netNaira, 100) * KOBO_PER_NAIRA;
  const markupMinor = roundTo(netNaira * markupRate, 100) * KOBO_PER_NAIRA;
  return { currency: 'NGN', sellMinor: netMinor + markupMinor, margin: { netMinor, markupMinor } };
}

function heldFrom(now: Date): { searchedAt: string; expiresAt: string } {
  return {
    searchedAt: now.toISOString(),
    expiresAt: new Date(now.getTime() + FARE_HOLD_MS).toISOString(),
  };
}

// --------------------------------------------------------------------------- flights

interface Carrier {
  code: string;
  name: string;
  /** Where it connects. Null for a carrier that flies the route direct. */
  hub: string | null;
}

const DOMESTIC_CARRIERS: readonly Carrier[] = [
  { code: 'P4', name: 'Air Peace', hub: null },
  { code: 'QI', name: 'Ibom Air', hub: null },
  { code: 'W3', name: 'Arik Air', hub: null },
  { code: 'UN', name: 'United Nigeria', hub: null },
  { code: 'VK', name: 'ValueJet', hub: null },
  { code: 'OF', name: 'Overland Airways', hub: null },
];

const REGIONAL_CARRIERS: readonly Carrier[] = [
  { code: 'P4', name: 'Air Peace', hub: null },
  { code: 'KP', name: 'ASKY Airlines', hub: 'LFW' },
  { code: 'AW', name: 'Africa World Airlines', hub: 'ACC' },
  { code: 'ET', name: 'Ethiopian Airlines', hub: 'ADD' },
];

const LONG_HAUL_CARRIERS: readonly Carrier[] = [
  { code: 'EK', name: 'Emirates', hub: 'DXB' },
  { code: 'QR', name: 'Qatar Airways', hub: 'DOH' },
  { code: 'ET', name: 'Ethiopian Airlines', hub: 'ADD' },
  { code: 'KQ', name: 'Kenya Airways', hub: 'NBO' },
  { code: 'TK', name: 'Turkish Airlines', hub: 'IST' },
  { code: 'BA', name: 'British Airways', hub: 'LHR' },
  { code: 'AF', name: 'Air France', hub: 'CDG' },
];

const WEST_AFRICA = new Set(['GH', 'TG', 'BJ', 'CI', 'SN']);

/** Rough block times from Nigeria, by the other country. Close enough to read true. */
const MINUTES_FROM_NIGERIA: Record<string, number> = {
  GH: 80,
  TG: 70,
  BJ: 45,
  CI: 120,
  SN: 240,
  ET: 300,
  KE: 330,
  ZA: 390,
  EG: 330,
  AE: 420,
  QA: 400,
  TR: 400,
  GB: 390,
  FR: 380,
  NL: 385,
  US: 690,
  CA: 760,
};

function blockMinutes(random: Random, from: Airport, to: Airport): number {
  if (from.country === 'NG' && to.country === 'NG') return between(random, 55, 95);
  const a = from.country === 'NG' ? 0 : (MINUTES_FROM_NIGERIA[from.country] ?? 300);
  const b = to.country === 'NG' ? 0 : (MINUTES_FROM_NIGERIA[to.country] ?? 300);
  const base =
    from.country === 'NG' || to.country === 'NG'
      ? Math.max(a, b)
      : Math.max(90, Math.abs(a - b) + 150);
  return roundTo(base + between(random, -20, 25), 5);
}

function carriersFor(origin: Airport, destination: Airport): readonly Carrier[] {
  if (origin.country === 'NG' && destination.country === 'NG') return DOMESTIC_CARRIERS;
  const abroad = origin.country === 'NG' ? destination : origin;
  return WEST_AFRICA.has(abroad.country) ? REGIONAL_CARRIERS : LONG_HAUL_CARRIERS;
}

function airport(code: string): Airport {
  const found = findAirport(code);
  if (!found) throw new ApiError(400, `${code} is not an airport we can search.`);
  return found;
}

function segment(
  random: Random,
  carrier: Carrier,
  from: Airport,
  to: Airport,
  departsAt: string,
  minutes: number,
): FlightSegment {
  return {
    carrierCode: carrier.code,
    carrierName: carrier.name,
    flightNumber: `${carrier.code} ${String(between(random, 100, 999)).padStart(4, '0')}`,
    origin: from.code,
    destination: to.code,
    departsAt,
    arrivesAt: addMinutes(departsAt, minutes),
    durationMinutes: minutes,
  };
}

const DEPARTURE_MINUTES = [0, 5, 10, 15, 20, 25, 30, 35, 40, 45, 50, 55] as const;

function journey(random: Random, carrier: Carrier, leg: FlightLeg): FlightJourney {
  const from = airport(leg.origin);
  const to = airport(leg.destination);
  const domestic = from.country === 'NG' && to.country === 'NG';
  const via =
    carrier.hub && carrier.hub !== from.code && carrier.hub !== to.code
      ? airport(carrier.hub)
      : null;
  const hour = domestic ? between(random, 6, 20) : between(random, 8, 23);
  const start = `${leg.date}T${pad(hour)}:${pad(pick(random, DEPARTURE_MINUTES))}`;

  if (!via) {
    const minutes = blockMinutes(random, from, to);
    return {
      segments: [segment(random, carrier, from, to, start, minutes)],
      durationMinutes: minutes,
      stops: 0,
    };
  }

  const firstMinutes = blockMinutes(random, from, via);
  const first = segment(random, carrier, from, via, start, firstMinutes);
  const layover = roundTo(between(random, 75, 260), 5);
  const secondMinutes = blockMinutes(random, via, to);
  const second = segment(
    random,
    carrier,
    via,
    to,
    addMinutes(first.arrivesAt, layover),
    secondMinutes,
  );

  return {
    segments: [first, second],
    durationMinutes: firstMinutes + layover + secondMinutes,
    stops: 1,
  };
}

interface Fare {
  fareFamily: string;
  refundable: boolean;
  cancellation: string;
  changes: string;
  bags: { domestic: string; abroad: string };
  factor: number;
  weight: number;
}

const ECONOMY_FARES: readonly Fare[] = [
  {
    fareFamily: 'Saver',
    refundable: false,
    cancellation: 'Non-refundable. Unused airport taxes can be claimed back.',
    changes: 'Date changes up to 24 hours before departure, for ₦15,000 plus any fare difference.',
    bags: { domestic: '15 kg', abroad: '1 × 23 kg' },
    factor: 1,
    weight: 0.55,
  },
  {
    fareFamily: 'Classic',
    refundable: true,
    cancellation: 'Refundable less ₦25,000, up to 24 hours before departure.',
    changes: 'Free date changes — you pay only any fare difference.',
    bags: { domestic: '23 kg', abroad: '2 × 23 kg' },
    factor: 1.22,
    weight: 0.3,
  },
  {
    fareFamily: 'Flex',
    refundable: true,
    cancellation: 'Fully refundable up to departure.',
    changes: 'Free changes, including on the day of travel.',
    bags: { domestic: '30 kg', abroad: '2 × 23 kg' },
    factor: 1.55,
    weight: 0.15,
  },
];

const PREMIUM_FARE: Fare = {
  fareFamily: 'Flex',
  refundable: true,
  cancellation: 'Refundable less ₦40,000, up to departure.',
  changes: 'Free changes, including on the day of travel.',
  bags: { domestic: '40 kg', abroad: '2 × 32 kg' },
  factor: 1,
  weight: 1,
};

const CABIN_FACTOR: Record<CabinClass, number> = {
  economy: 1,
  premium_economy: 1.7,
  business: 3.2,
  first: 5.2,
};

function fareFor(random: Random, cabin: CabinClass): Fare {
  if (cabin !== 'economy') return PREMIUM_FARE;
  let roll = random();
  for (const fare of ECONOMY_FARES) {
    roll -= fare.weight;
    if (roll <= 0) return fare;
  }
  return ECONOMY_FARES[0] ?? PREMIUM_FARE;
}

function netPerAdultNaira(random: Random, origin: Airport, destination: Airport): number {
  if (origin.country === 'NG' && destination.country === 'NG') {
    return roundTo(between(random, 78_000, 165_000), 500);
  }
  const abroad = origin.country === 'NG' ? destination : origin;
  if (WEST_AFRICA.has(abroad.country)) return roundTo(between(random, 380_000, 720_000), 1_000);
  return roundTo(between(random, 1_150_000, 2_900_000), 5_000);
}

// ------------------------------------------------------------------------------ buses

interface Operator {
  name: string;
  vehicle: string;
  seats: number;
  amenities: string[];
  premium: number;
  terms: { cancellation: string; luggage: string };
}

const FLEXIBLE_BUS = {
  cancellation: 'Refundable less 10%, up to 12 hours before departure.',
  luggage: 'One bag up to 20 kg in the hold.',
};
const FIXED_BUS = {
  cancellation: 'Non-refundable. One free date change, up to 24 hours before.',
  luggage: 'One bag up to 10 kg.',
};

const OPERATORS: readonly Operator[] = [
  {
    name: 'GIG Mobility',
    vehicle: 'Toyota Hiace · 14 seats',
    seats: 14,
    amenities: ['Air conditioning', 'Wi-Fi', 'USB charging'],
    premium: 1.15,
    terms: FLEXIBLE_BUS,
  },
  {
    name: 'ABC Transport',
    vehicle: 'Marcopolo coach · 59 seats',
    seats: 59,
    amenities: ['Air conditioning', 'Toilet', 'Snack on board'],
    premium: 1.05,
    terms: FLEXIBLE_BUS,
  },
  {
    name: 'Chisco Transport',
    vehicle: 'Coach · 52 seats',
    seats: 52,
    amenities: ['Air conditioning'],
    premium: 1,
    terms: FIXED_BUS,
  },
  {
    name: 'Peace Mass Transit',
    vehicle: 'Toyota Hiace · 14 seats',
    seats: 14,
    amenities: ['Air conditioning'],
    premium: 0.9,
    terms: FIXED_BUS,
  },
  {
    name: 'Libra Motors',
    vehicle: 'Toyota Sienna · 6 seats',
    seats: 6,
    amenities: ['Air conditioning', 'Executive seats'],
    premium: 1.35,
    terms: FLEXIBLE_BUS,
  },
  {
    name: 'GUO Transport',
    vehicle: 'Coach · 48 seats',
    seats: 48,
    amenities: ['Air conditioning', 'Reclining seats'],
    premium: 1,
    terms: FIXED_BUS,
  },
];

/** Road hours between cities, keyed by the two names in alphabetical order. */
const ROUTE_HOURS: Record<string, number> = {
  'Abuja|Lagos': 11,
  'Benin City|Lagos': 5.5,
  'Ibadan|Lagos': 2,
  'Enugu|Lagos': 9,
  'Lagos|Port Harcourt': 10.5,
  'Lagos|Owerri': 9.5,
  'Lagos|Onitsha': 7.5,
  'Asaba|Lagos': 7,
  'Lagos|Warri': 7,
  'Kaduna|Lagos': 12,
  'Abuja|Kaduna': 3,
  'Abuja|Enugu': 6.5,
  'Abuja|Ibadan': 9.5,
  'Abuja|Benin City': 7.5,
  'Enugu|Port Harcourt': 4,
  'Benin City|Onitsha': 2.5,
};

function routeHours(from: BusTerminal, to: BusTerminal): number {
  return ROUTE_HOURS[[from.city, to.city].sort().join('|')] ?? 6;
}

function terminal(id: string): BusTerminal {
  const found = findTerminal(id);
  if (!found) throw new ApiError(400, 'That bus terminal is not one we can search.');
  return found;
}

const BUS_DEPARTURE_MINUTES = [0, 15, 30, 45] as const;

function departures(
  random: Random,
  from: BusTerminal,
  to: BusTerminal,
  date: string,
  passengers: number,
): BusOffer[] {
  const hours = routeHours(from, to);
  const count = between(random, 6, 9);
  const offers: BusOffer[] = [];

  for (let index = 0; index < count; index++) {
    const operator = pick(random, OPERATORS);
    // Long routes leave at dawn, so the bus is not on the road after dark.
    const hour = hours > 6 ? between(random, 5, 9) : between(random, 5, 16);
    const departsAt = `${date}T${pad(hour)}:${pad(pick(random, BUS_DEPARTURE_MINUTES))}`;
    const minutes = roundTo(
      hours * 60 * (operator.seats <= 14 ? 0.92 : 1.05) + between(random, -20, 40),
      5,
    );
    const perSeatNaira = Math.max(
      4_500,
      roundTo(hours * between(random, 2_100, 2_900) * operator.premium, 500),
    );

    offers.push({
      id: `bus_${from.id}_${to.id}_${date}_${index}`,
      operator: operator.name,
      vehicle: operator.vehicle,
      departureTerminal: from,
      arrivalTerminal: to,
      departsAt,
      arrivesAt: addMinutes(departsAt, minutes),
      durationMinutes: minutes,
      availableSeats: random() < 0.12 ? 0 : between(random, 1, Math.min(operator.seats, 18)),
      amenities: operator.amenities,
      terms: operator.terms,
      price: price(perSeatNaira * passengers, 0.1),
    });
  }

  return offers;
}

// ----------------------------------------------------------------------------- adapter

export const mockSearchApi: SearchApi = {
  async searchFlights(criteria, signal) {
    await wait(LATENCY_MS, signal);

    const first = criteria.legs[0];
    if (!first) throw new ApiError(400, 'A search needs at least one flight.');
    if (first.origin === TIMEOUT_ROUTE.origin && first.destination === TIMEOUT_ROUTE.destination) {
      throw new ApiError(504, 'The airline systems did not answer in time.');
    }

    const held = heldFrom(new Date());
    if (
      first.origin === NOTHING_FLIES_ROUTE.origin &&
      first.destination === NOTHING_FLIES_ROUTE.destination
    ) {
      return { ...held, offers: [] };
    }

    const random = seeded(JSON.stringify(criteria));
    const origin = airport(first.origin);
    const destination = airport(first.destination);
    const domestic = origin.country === 'NG' && destination.country === 'NG';
    const carriers = carriersFor(origin, destination);
    const { adults, children, infants } = criteria.passengers;
    const passengerFactor = adults + children * 0.75 + infants * 0.1;
    const legFactor = criteria.legs.length === 1 ? 1 : criteria.legs.length * 0.93;
    const markupRate = domestic ? 0.08 : 0.05;

    const offers: FlightOffer[] = [];
    for (let index = 0; index < (domestic ? 14 : 16); index++) {
      const carrier = pick(random, carriers);
      const fare = fareFor(random, criteria.cabin);
      const journeys = criteria.legs.map((leg) => journey(random, carrier, leg));
      // A connection is cheaper than a direct flight — which is why anyone takes one.
      const connectionDiscount = journeys.some((trip) => trip.stops > 0) ? 0.9 : 1;
      const perAdult =
        netPerAdultNaira(random, origin, destination) *
        fare.factor *
        CABIN_FACTOR[criteria.cabin] *
        connectionDiscount;

      const terms: FareTerms = {
        fareFamily: fare.fareFamily,
        refundable: fare.refundable,
        cancellation: fare.cancellation,
        changes: fare.changes,
        checkedBaggage: domestic ? fare.bags.domestic : fare.bags.abroad,
        cabinBaggage: '7 kg',
      };

      offers.push({
        id: `fl_${index}_${Math.floor(random() * 1e9).toString(36)}`,
        journeys,
        cabin: criteria.cabin,
        seatsLeft: random() < 0.35 ? between(random, 1, 4) : null,
        terms,
        price: price(perAdult * passengerFactor * legFactor, markupRate),
      });
    }

    return { ...held, offers };
  },

  async searchBuses(criteria, signal) {
    await wait(LATENCY_MS, signal);

    const from = terminal(criteria.departureTerminalId);
    const to = terminal(criteria.arrivalTerminalId);
    const random = seeded(JSON.stringify(criteria));

    return {
      ...heldFrom(new Date()),
      offers: departures(random, from, to, criteria.date, criteria.passengers),
      returnOffers:
        criteria.tripType === 'round_trip' && criteria.returnDate
          ? departures(random, to, from, criteria.returnDate, criteria.passengers)
          : null,
    };
  },
};
