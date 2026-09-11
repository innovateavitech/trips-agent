import type {
  BookingDetail,
  BookingStatus,
  BookingTraveller,
  PaidFrom,
  ProductKind,
  TimelineEntry,
} from '../types';

/**
 * ============================================================================
 *  TEMPORARY. Delete with the rest of `mock/` when the orders API lands (#42, #44).
 * ============================================================================
 *
 * One in-memory list of bookings, shared by the bookings screens' stand-in and
 * the booking flow's — so a booking made in the flow shows up in Bookings, as it
 * will against the real API. Seeded with the same references the dashboard's
 * stand-in shows, so the dashboard's links open bookings that exist.
 *
 * It lives as long as the page. A reload starts again.
 */

const MINUTE = 60_000;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;
const LAGOS_OFFSET_MS = HOUR;

let store: BookingDetail[] | null = null;

/** Bookings due to change on their own — a ticket landing, or a supplier giving up. */
const settlements = new Map<
  string,
  { at: number; outcome: 'ticketed' | 'failed'; reason: string }
>();

const iso = (instantMs: number) => new Date(instantMs).toISOString();

/** A Lagos wall-clock time, `YYYY-MM-DDTHH:mm`, as a ticket prints it. */
export const lagosWallClock = (instantMs: number) =>
  new Date(instantMs + LAGOS_OFFSET_MS).toISOString().slice(0, 16);

/** The markup inside a sell price, at the 8% the stand-in agency charges. Whole naira. */
export function marginOf(sellMinor: number): { netMinor: number; markupMinor: number } {
  const markupMinor = Math.round((sellMinor * 8) / 108 / 100) * 100;
  return { netMinor: sellMinor - markupMinor, markupMinor };
}

/** A stable six-character code from any text, for PNRs and references. */
export function codeFrom(text: string): string {
  let hash = 2166136261;
  for (let i = 0; i < text.length; i++) {
    hash ^= text.charCodeAt(i);
    hash = Math.imul(hash, 16777619);
  }
  return (hash >>> 0).toString(36).toUpperCase().padStart(6, 'X').slice(0, 6);
}

function ticketNumber(reference: string, index: number): string {
  const digits = Array.from(`${reference}${index}`, (char) => char.charCodeAt(0) % 10).join('');
  return `074${digits}`.padEnd(13, '0').slice(0, 13);
}

interface Seed {
  reference: string;
  travellers: Array<[name: string, type: BookingTraveller['type']]>;
  product: ProductKind;
  origin: string;
  destination: string;
  carrier: string;
  departsInMs: number;
  durationMs: number;
  status: BookingStatus;
  sellMinor: number;
  ticketTimeLimitInMs: number | null;
  pnr: string | null;
  bookedAgoMs: number;
  paidFrom: PaidFrom;
  failure?: string;
}

function build(seed: Seed): BookingDetail {
  const now = Date.now();
  const bookedAt = now - seed.bookedAgoMs;
  const departs = now + seed.departsInMs;
  const ticketed = seed.status === 'ticketed';

  return {
    reference: seed.reference,
    leadTraveller: seed.travellers[0]?.[0] ?? '',
    travellerCount: seed.travellers.length,
    product: seed.product,
    origin: seed.origin,
    destination: seed.destination,
    carrier: seed.carrier,
    departsAt: iso(departs),
    status: seed.status,
    sellMinor: seed.sellMinor,
    currency: 'NGN',
    ticketTimeLimit: seed.ticketTimeLimitInMs === null ? null : iso(now + seed.ticketTimeLimitInMs),
    pnr: seed.pnr,
    bookedAt: iso(bookedAt),
    paidFrom: seed.paidFrom,
    travellers: seed.travellers.map(([name, type], index) => ({
      name,
      type,
      ticketNumber:
        ticketed && seed.product === 'flight' ? ticketNumber(seed.reference, index) : null,
    })),
    segments: [
      {
        carrier: seed.carrier,
        origin: seed.origin,
        destination: seed.destination,
        departsAt: lagosWallClock(departs),
        arrivesAt: lagosWallClock(departs + seed.durationMs),
      },
    ],
    price: { sellMinor: seed.sellMinor, margin: marginOf(seed.sellMinor) },
    timeline: timelineFor(seed, bookedAt),
    failure: seed.failure
      ? { reason: seed.failure, atRiskMinor: seed.sellMinor, paidFrom: seed.paidFrom }
      : null,
  };
}

function timelineFor(seed: Seed, bookedAt: number): TimelineEntry[] {
  const paid: TimelineEntry = {
    at: iso(bookedAt),
    status: 'awaiting_ticket',
    note:
      seed.paidFrom === 'wallet'
        ? 'Booked and paid from your wallet'
        : "Booked, paid on the customer's card",
  };

  switch (seed.status) {
    case 'ticketed':
      return [
        paid,
        { at: iso(bookedAt + 4 * MINUTE), status: 'ticketed', note: `Ticketed — PNR ${seed.pnr}` },
      ];
    case 'failed':
      return [
        paid,
        {
          at: iso(bookedAt + 12 * MINUTE),
          status: 'failed',
          note: seed.failure ?? 'The supplier did not confirm',
        },
      ];
    case 'confirmed':
      return [{ at: iso(bookedAt), status: 'confirmed', note: `Seats held with ${seed.carrier}` }];
    default:
      return [paid];
  }
}

function seed(): BookingDetail[] {
  return [
    {
      reference: 'TRP-8K2P9A',
      travellers: [['Emeka Nwosu', 'ADT']],
      product: 'flight',
      origin: 'PHC',
      destination: 'LOS',
      carrier: 'Ibom Air QI 0321',
      departsInMs: 26 * HOUR,
      durationMs: 70 * MINUTE,
      status: 'awaiting_ticket',
      sellMinor: 11_890_000,
      ticketTimeLimitInMs: 2 * HOUR + 40 * MINUTE,
      pnr: null,
      bookedAgoMs: 3 * HOUR,
      paidFrom: 'wallet',
    },
    {
      reference: 'TRP-8K2L1E',
      travellers: [
        ['Ngozi Eze', 'ADT'],
        ['Chidi Eze', 'ADT'],
      ],
      product: 'flight',
      origin: 'QOW',
      destination: 'LOS',
      carrier: 'Arik Air W3 0112',
      departsInMs: 3 * DAY,
      durationMs: 65 * MINUTE,
      status: 'failed',
      sellMinor: 20_950_000,
      ticketTimeLimitInMs: null,
      pnr: null,
      bookedAgoMs: 5 * HOUR,
      paidFrom: 'wallet',
      failure: 'Arik Air did not confirm the seats before the fare expired.',
    },
    {
      reference: 'TRP-8K2R6H',
      travellers: [
        ['Folake Adeyemi', 'ADT'],
        ['Bayo Adeyemi', 'ADT'],
        ['Tobi Adeyemi', 'CHD'],
        ['Kemi Adeyemi', 'INF'],
      ],
      product: 'flight',
      origin: 'LOS',
      destination: 'KAN',
      carrier: 'United Nigeria UN 0213',
      departsInMs: 5 * DAY,
      durationMs: 95 * MINUTE,
      status: 'awaiting_ticket',
      sellMinor: 38_200_000,
      ticketTimeLimitInMs: 20 * HOUR,
      pnr: null,
      bookedAgoMs: 1 * HOUR,
      paidFrom: 'card',
    },
    {
      reference: 'TRP-8K2Q4F',
      travellers: [['Adaeze Okafor', 'ADT']],
      product: 'flight',
      origin: 'LOS',
      destination: 'ABV',
      carrier: 'Air Peace P4 7121',
      departsInMs: 18 * HOUR,
      durationMs: 75 * MINUTE,
      status: 'ticketed',
      sellMinor: 14_250_000,
      ticketTimeLimitInMs: null,
      pnr: 'QX7K2P',
      bookedAgoMs: 1 * DAY,
      paidFrom: 'wallet',
    },
    {
      reference: 'TRP-8K2M7D',
      travellers: [
        ['Tunde Bakare', 'ADT'],
        ['Sade Bakare', 'ADT'],
        ['Femi Bakare', 'ADT'],
      ],
      product: 'bus',
      origin: 'Lagos',
      destination: 'Abuja',
      carrier: 'GIG Mobility',
      departsInMs: 40 * HOUR,
      durationMs: 11 * HOUR,
      status: 'ticketed',
      sellMinor: 8_700_000,
      ticketTimeLimitInMs: null,
      pnr: 'GIG7Q4',
      bookedAgoMs: 2 * DAY,
      paidFrom: 'wallet',
    },
    {
      reference: 'TRP-8K2N3C',
      travellers: [['Hauwa Bello', 'ADT']],
      product: 'flight',
      origin: 'KAN',
      destination: 'ABV',
      carrier: 'United Nigeria UN 0503',
      departsInMs: 2 * DAY,
      durationMs: 70 * MINUTE,
      status: 'ticketed',
      sellMinor: 9_640_000,
      ticketTimeLimitInMs: null,
      pnr: 'UN7K3D',
      bookedAgoMs: 3 * DAY,
      paidFrom: 'card',
    },
    {
      reference: 'TRP-8K2K5G',
      travellers: [
        ['Ibrahim Musa', 'ADT'],
        ['Aisha Musa', 'ADT'],
      ],
      product: 'bus',
      origin: 'Abuja',
      destination: 'Kano',
      carrier: 'ABC Transport',
      departsInMs: 4 * DAY,
      durationMs: 6 * HOUR,
      status: 'confirmed',
      sellMinor: 4_300_000,
      ticketTimeLimitInMs: null,
      pnr: null,
      bookedAgoMs: 4 * HOUR,
      paidFrom: 'wallet',
    },
  ].map((entry) => build(entry as Seed));
}

export function allBookings(): BookingDetail[] {
  store ??= seed();
  settleDue();
  return store;
}

export function findBooking(reference: string): BookingDetail | undefined {
  return allBookings().find((booking) => booking.reference === reference);
}

export function addBooking(booking: BookingDetail): void {
  allBookings().unshift(booking);
}

export function updateBooking(
  reference: string,
  change: (booking: BookingDetail) => BookingDetail,
): BookingDetail | undefined {
  const bookings = store ?? allBookings();
  const index = bookings.findIndex((booking) => booking.reference === reference);
  if (index < 0) return undefined;
  const next = change(bookings[index]!);
  bookings[index] = next;
  return next;
}

/** Arranges for a booking to settle on its own after a while, as a real ticket does. */
export function scheduleSettlement(
  reference: string,
  outcome: 'ticketed' | 'failed',
  afterMs: number,
  reason = 'The supplier did not confirm the booking.',
): void {
  settlements.set(reference, { at: Date.now() + afterMs, outcome, reason });
}

function settleDue(): void {
  const now = Date.now();

  for (const [reference, settlement] of settlements) {
    if (settlement.at > now) continue;
    settlements.delete(reference);

    updateBooking(reference, (booking) => {
      if (settlement.outcome === 'failed') {
        return {
          ...booking,
          status: 'failed',
          ticketTimeLimit: null,
          failure: {
            reason: settlement.reason,
            atRiskMinor: booking.sellMinor,
            paidFrom: booking.paidFrom,
          },
          timeline: [
            ...booking.timeline,
            { at: iso(now), status: 'failed', note: settlement.reason },
          ],
        };
      }

      const pnr = codeFrom(reference);
      return {
        ...booking,
        status: 'ticketed',
        pnr,
        ticketTimeLimit: null,
        failure: null,
        travellers: booking.travellers.map((traveller, index) => ({
          ...traveller,
          ticketNumber: booking.product === 'flight' ? ticketNumber(reference, index) : null,
        })),
        timeline: [
          ...booking.timeline,
          { at: iso(now), status: 'ticketed', note: `Ticketed — PNR ${pnr}` },
        ],
      };
    });
  }
}

/** For tests: forget everything, so each one starts from the seed. */
export function resetBookingStore(): void {
  store = null;
  settlements.clear();
}
