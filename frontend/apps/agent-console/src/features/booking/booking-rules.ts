import { findAirport } from '../search/reference-data';
import type { OfferPrice, Passengers } from '../search/types';
import type { BookingDraft, PriceConfirmation, TravellerDetails, TravellerType } from './types';

/**
 * Everything the booking flow decides, as plain functions — tested without
 * rendering anything in `__tests__/booking-rules.test.ts`.
 */

/** Field key → what is wrong with it, in words the agent can act on. Empty when valid. */
export type Problems = Record<string, string>;

const NAME = /^[\p{L}][\p{L}' -]*$/u;
const EMAIL = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;
const PHONE = /^\+?[0-9 ]{7,17}$/;
const PASSPORT = /^[A-Z0-9]{6,9}$/i;

/** One slot per traveller, adults first — the lead traveller is always an adult. */
export function travellerSlots({ adults, children, infants }: Passengers): TravellerType[] {
  return [
    ...Array<TravellerType>(adults).fill('ADT'),
    ...Array<TravellerType>(children).fill('CHD'),
    ...Array<TravellerType>(infants).fill('INF'),
  ];
}

const TYPE_WORD: Record<TravellerType, string> = { ADT: 'Adult', CHD: 'Child', INF: 'Infant' };

/** "Adult 1", "Adult 2", "Child 1" — numbered within each type, as an agent reads a party. */
export function slotLabels(types: readonly TravellerType[]): string[] {
  const seen: Record<TravellerType, number> = { ADT: 0, CHD: 0, INF: 0 };
  return types.map((type) => `${TYPE_WORD[type]} ${(seen[type] += 1)}`);
}

export function emptyTraveller(type: TravellerType): TravellerDetails {
  return {
    type,
    title: '',
    firstName: '',
    lastName: '',
    dateOfBirth: '',
    gender: '',
    email: '',
    phone: '',
    passportNumber: '',
    passportExpiry: '',
    nationality: 'NG',
  };
}

/**
 * Whether the airline needs passports: any airport outside Nigeria. An airport
 * we do not recognise counts as abroad — asking for a passport nobody needed
 * costs a minute; not asking for one that was needed costs the booking.
 */
export function needsPassport(draft: BookingDraft): boolean {
  if (draft.product === 'bus') return false;

  return draft.offer.journeys.some((journey) =>
    journey.segments.some((segment) =>
      [segment.origin, segment.destination].some((code) => findAirport(code)?.country !== 'NG'),
    ),
  );
}

/** The local date of the first departure, `YYYY-MM-DD`: the day ages are counted on. */
export function departureDate(draft: BookingDraft): string {
  return draft.product === 'flight'
    ? (draft.offer.journeys[0]?.segments[0]?.departsAt.slice(0, 10) ?? '')
    : draft.offer.departsAt.slice(0, 10);
}

/** Whole years between two `YYYY-MM-DD` dates — how old someone is on the day. */
export function ageOn(dateOfBirth: string, onDate: string): number {
  const [bornYear = 0, bornMonth = 0, bornDay = 0] = dateOfBirth.split('-').map(Number);
  const [year = 0, month = 0, day = 0] = onDate.split('-').map(Number);

  const hadBirthday = month > bornMonth || (month === bornMonth && day >= bornDay);
  return year - bornYear - (hadBirthday ? 0 : 1);
}

export function validateTraveller(
  traveller: TravellerDetails,
  {
    isLead,
    needsPassport,
    travelDate,
  }: { isLead: boolean; needsPassport: boolean; travelDate: string },
): Problems {
  const problems: Problems = {};

  for (const [field, label] of [
    ['firstName', 'first name'],
    ['lastName', 'last name'],
  ] as const) {
    const value = traveller[field].trim();
    if (!value) problems[field] = `Enter the ${label}.`;
    else if (!NAME.test(value)) problems[field] = 'Letters, spaces, hyphens and apostrophes only.';
  }

  // The fare depends on a child's or infant's age; the airline wants every date of birth with a passport.
  const birthRequired = traveller.type !== 'ADT' || needsPassport;

  if (!traveller.dateOfBirth) {
    if (birthRequired) {
      problems['dateOfBirth'] =
        traveller.type === 'ADT'
          ? 'Enter the date of birth — the airline needs it with a passport.'
          : 'Enter the date of birth — the fare depends on age.';
    }
  } else if (traveller.dateOfBirth > travelDate) {
    problems['dateOfBirth'] = 'That date is after the trip.';
  } else {
    const age = ageOn(traveller.dateOfBirth, travelDate);

    if (traveller.type === 'INF' && age >= 2) {
      problems['dateOfBirth'] = 'An infant is under 2 on the day of travel. Book them as a child.';
    } else if (traveller.type === 'CHD' && age < 2) {
      problems['dateOfBirth'] = 'Under 2 on the day of travel is an infant, on an adult’s lap.';
    } else if (traveller.type === 'CHD' && age > 11) {
      problems['dateOfBirth'] = 'A child is 2 to 11 on the day of travel. Book them as an adult.';
    } else if (traveller.type === 'ADT' && age < 12) {
      problems['dateOfBirth'] = 'An adult is 12 or over on the day of travel.';
    }
  }

  if (isLead) {
    if (!EMAIL.test(traveller.email.trim()))
      problems['email'] = 'An email address, for schedule changes.';
    if (!PHONE.test(traveller.phone.trim()))
      problems['phone'] = 'A phone number, for schedule changes.';
  }

  if (needsPassport) {
    if (!PASSPORT.test(traveller.passportNumber.trim())) {
      problems['passportNumber'] = 'Six to nine letters and numbers, as printed.';
    }
    if (!traveller.passportExpiry) {
      problems['passportExpiry'] = 'Enter the expiry date.';
    } else if (traveller.passportExpiry <= travelDate) {
      problems['passportExpiry'] = 'This passport expires before the trip.';
    }
    if (!/^[A-Z]{2}$/.test(traveller.nationality.trim().toUpperCase())) {
      problems['nationality'] = 'Two letters, like NG.';
    }
  }

  return problems;
}

export function priceChanged(confirmation: PriceConfirmation): boolean {
  // Only a rise needs the agent to accept it again; a fall passes straight through (decision Q9).
  return confirmation.sellMinor > confirmation.searchedSellMinor;
}

/** Undefined while the balance is still loading: "not yet known" is not "not enough". */
export function canPayFromWallet(
  balanceMinor: number | undefined,
  sellMinor: number,
): boolean | undefined {
  return balanceMinor === undefined ? undefined : balanceMinor >= sellMinor;
}

/**
 * The margin after the supplier moved the price. The supplier's price is the net
 * rate, so a rise lands on the net and the agency's markup stays what it was.
 */
export function confirmedMargin(
  margin: OfferPrice['margin'],
  confirmation: PriceConfirmation,
): OfferPrice['margin'] {
  if (!margin) return null;
  return {
    netMinor: margin.netMinor + (confirmation.sellMinor - confirmation.searchedSellMinor),
    markupMinor: margin.markupMinor,
  };
}

export function describeRoute(draft: BookingDraft): string {
  if (draft.product === 'bus') {
    return `${draft.offer.departureTerminal.city} → ${draft.offer.arrivalTerminal.city} · ${draft.offer.operator}`;
  }

  return draft.offer.journeys
    .map((journey) => {
      const first = journey.segments[0];
      const last = journey.segments[journey.segments.length - 1];
      return first && last ? `${first.origin} → ${last.destination}` : '';
    })
    .filter(Boolean)
    .join(' · ');
}

/** Who the agent is waiting on: "Ibom Air", "GIG Mobility". */
export function carrierOf(draft: BookingDraft): string {
  return draft.product === 'flight'
    ? (draft.offer.journeys[0]?.segments[0]?.carrierName ?? 'the airline')
    : draft.offer.operator;
}

/**
 * The fare the agent chose, from the navigation state the search results left.
 * Null when there is none — someone opened `/book/…` cold, or reloaded after the
 * browser dropped the state.
 */
export function readDraft(state: unknown): BookingDraft | null {
  if (!state || typeof state !== 'object' || !('draft' in state)) return null;

  const draft = (state as { draft: unknown }).draft;
  if (!draft || typeof draft !== 'object') return null;

  const candidate = draft as Partial<BookingDraft>;
  return (candidate.product === 'flight' || candidate.product === 'bus') &&
    candidate.offer &&
    candidate.passengers
    ? (candidate as BookingDraft)
    : null;
}
