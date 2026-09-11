import { describe, expect, it } from 'vitest';
import {
  ageOn,
  canPayFromWallet,
  confirmedMargin,
  emptyTraveller,
  needsPassport,
  priceChanged,
  readDraft,
  slotLabels,
  travellerSlots,
  validateTraveller,
} from '../booking-rules';
import type { BusOffer, FlightOffer } from '../../search/types';
import type { BookingDraft, PriceConfirmation, TravellerDetails } from '../types';

const TRAVEL_DATE = '2026-10-02';

function flightOffer(route: Array<[string, string]>): FlightOffer {
  return {
    id: 'fl_1',
    cabin: 'economy',
    seatsLeft: null,
    journeys: route.map(([origin, destination]) => ({
      durationMinutes: 75,
      stops: 0,
      segments: [
        {
          carrierCode: 'P4',
          carrierName: 'Air Peace',
          flightNumber: 'P4 0100',
          origin,
          destination,
          departsAt: `${TRAVEL_DATE}T07:30`,
          arrivesAt: `${TRAVEL_DATE}T08:45`,
          durationMinutes: 75,
        },
      ],
    })),
    terms: {
      fareFamily: 'Saver',
      refundable: false,
      cancellation: 'Non-refundable.',
      changes: 'None.',
      checkedBaggage: '20 kg',
      cabinBaggage: '7 kg',
    },
    price: {
      currency: 'NGN',
      sellMinor: 10_000_000,
      margin: { netMinor: 9_000_000, markupMinor: 1_000_000 },
    },
  };
}

const busOffer: BusOffer = {
  id: 'bus_1',
  operator: 'GIG Mobility',
  vehicle: 'Toyota Hiace · 14 seats',
  departureTerminal: { id: 't1', city: 'Lagos', name: 'Jibowu' },
  arrivalTerminal: { id: 't2', city: 'Abuja', name: 'Utako' },
  departsAt: `${TRAVEL_DATE}T06:00`,
  arrivesAt: `${TRAVEL_DATE}T17:00`,
  durationMinutes: 660,
  availableSeats: 4,
  amenities: [],
  terms: { cancellation: 'Refundable less 10%.', luggage: 'One bag.' },
  price: { currency: 'NGN', sellMinor: 3_000_000, margin: null },
};

const passengers = { adults: 1, children: 0, infants: 0 };
const domestic: BookingDraft = {
  product: 'flight',
  offer: flightOffer([['LOS', 'ABV']]),
  passengers,
};
const international: BookingDraft = {
  product: 'flight',
  offer: flightOffer([['LOS', 'LHR']]),
  passengers,
};
const bus: BookingDraft = { product: 'bus', offer: busOffer, passengers };

function traveller(patch: Partial<TravellerDetails> = {}): TravellerDetails {
  return { ...emptyTraveller('ADT'), firstName: 'Ngozi', lastName: 'Adeyemi', ...patch };
}

const asLead = { isLead: true, needsPassport: false, travelDate: TRAVEL_DATE };
const asOther = { isLead: false, needsPassport: false, travelDate: TRAVEL_DATE };

describe('the travellers a party needs', () => {
  it('is one slot per passenger, adults first, so the lead is always an adult', () => {
    expect(travellerSlots({ adults: 2, children: 1, infants: 1 })).toEqual([
      'ADT',
      'ADT',
      'CHD',
      'INF',
    ]);
  });

  it('numbers each slot within its type', () => {
    expect(slotLabels(['ADT', 'ADT', 'CHD', 'INF'])).toEqual([
      'Adult 1',
      'Adult 2',
      'Child 1',
      'Infant 1',
    ]);
  });
});

describe('when passports are needed', () => {
  it('is only when the route leaves Nigeria', () => {
    expect(needsPassport(domestic)).toBe(false);
    expect(needsPassport(international)).toBe(true);
    expect(needsPassport(bus)).toBe(false);
  });

  it('treats an airport we do not recognise as abroad, because asking costs less than not asking', () => {
    expect(needsPassport({ ...domestic, offer: flightOffer([['LOS', 'ZZZ']]) })).toBe(true);
  });
});

describe('checking a traveller', () => {
  it('accepts a complete lead traveller on a domestic flight', () => {
    expect(
      validateTraveller(traveller({ email: 'ngozi@example.test', phone: '08030000000' }), asLead),
    ).toEqual({});
  });

  it('needs a first and last name', () => {
    const problems = validateTraveller(traveller({ firstName: '', lastName: ' ' }), asOther);

    expect(problems['firstName']).toBeDefined();
    expect(problems['lastName']).toBeDefined();
  });

  it('needs the lead traveller’s email and phone, for schedule changes', () => {
    const problems = validateTraveller(traveller(), asLead);

    expect(problems['email']).toBeDefined();
    expect(problems['phone']).toBeDefined();
  });

  it('refuses an infant who is 2 on the day of travel', () => {
    const problems = validateTraveller(
      traveller({ type: 'INF', dateOfBirth: '2024-09-01' }),
      asOther,
    );

    expect(problems['dateOfBirth']).toMatch(/Book them as a child/);
    expect(
      validateTraveller(traveller({ type: 'INF', dateOfBirth: '2025-05-01' }), asOther),
    ).toEqual({});
  });

  it('keeps a child between 2 and 11 on the day of travel', () => {
    expect(
      validateTraveller(traveller({ type: 'CHD', dateOfBirth: '2015-01-01' }), asOther),
    ).toEqual({});
    expect(
      validateTraveller(traveller({ type: 'CHD', dateOfBirth: '2014-01-01' }), asOther)[
        'dateOfBirth'
      ],
    ).toMatch(/Book them as an adult/);
    expect(
      validateTraveller(traveller({ type: 'CHD', dateOfBirth: '2025-01-01' }), asOther)[
        'dateOfBirth'
      ],
    ).toMatch(/infant/);
  });

  it('needs a child’s date of birth, because the fare depends on it', () => {
    expect(validateTraveller(traveller({ type: 'CHD' }), asOther)['dateOfBirth']).toBeDefined();
  });

  it('asks for a passport, its expiry and the date of birth when the route leaves Nigeria', () => {
    const problems = validateTraveller(traveller(), { ...asOther, needsPassport: true });

    expect(Object.keys(problems)).toEqual(
      expect.arrayContaining(['passportNumber', 'passportExpiry', 'dateOfBirth']),
    );
  });

  it('refuses a passport that expires before the trip', () => {
    const problems = validateTraveller(
      traveller({
        dateOfBirth: '1990-01-01',
        passportNumber: 'A01234567',
        passportExpiry: '2026-09-30',
      }),
      { ...asOther, needsPassport: true },
    );

    expect(problems['passportExpiry']).toMatch(/expires before the trip/);
  });

  it('counts age on the day of travel, birthday included', () => {
    expect(ageOn('2014-10-02', TRAVEL_DATE)).toBe(12);
    expect(ageOn('2014-10-03', TRAVEL_DATE)).toBe(11);
  });
});

describe('money', () => {
  const confirmation = (sellMinor: number): PriceConfirmation => ({
    sellMinor,
    searchedSellMinor: 10_000_000,
    currency: 'NGN',
    ticketTimeLimit: '2026-10-01T10:00:00Z',
  });

  it('notices when the supplier moved the price', () => {
    expect(priceChanged(confirmation(10_000_000))).toBe(false);
    expect(priceChanged(confirmation(10_350_000))).toBe(true);
  });

  it('puts a supplier’s price rise on the net rate, leaving the markup alone', () => {
    expect(
      confirmedMargin({ netMinor: 9_000_000, markupMinor: 1_000_000 }, confirmation(10_350_000)),
    ).toEqual({
      netMinor: 9_350_000,
      markupMinor: 1_000_000,
    });
    expect(confirmedMargin(null, confirmation(10_350_000))).toBeNull();
  });

  it('does not call a balance that is still loading "not enough"', () => {
    expect(canPayFromWallet(undefined, 100)).toBeUndefined();
    expect(canPayFromWallet(99, 100)).toBe(false);
    expect(canPayFromWallet(100, 100)).toBe(true);
  });
});

describe('the fare carried in from the search', () => {
  it('is read back from the navigation state, and nothing else passes for one', () => {
    expect(readDraft({ draft: domestic })).toEqual(domestic);
    expect(readDraft(undefined)).toBeNull();
    expect(readDraft({})).toBeNull();
    expect(readDraft({ draft: { product: 'boat', offer: {}, passengers } })).toBeNull();
  });
});
