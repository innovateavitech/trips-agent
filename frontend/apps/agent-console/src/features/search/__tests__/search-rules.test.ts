import { describe, expect, it } from 'vitest';
import { AIRPORTS, BUS_TERMINALS } from '../reference-data';
import {
  addDays,
  applyFlightFilters,
  canViewMargin,
  dayOffset,
  describePassengers,
  formatDuration,
  matchAirports,
  NO_FLIGHT_FILTERS,
  redactBusResult,
  redactFlightResult,
  secondsUntil,
  sortFlights,
  timeOfDay,
  validateBusCriteria,
  validateFlightCriteria,
} from '../search-rules';
import type { BusSearchCriteria, FlightOffer, FlightSearchCriteria, SearchResult } from '../types';

const TODAY = '2026-09-11';

function offer(
  id: string,
  { sell = 10_000_000, stops = 0, carrier = 'P4', departs = '2026-09-14T07:30', duration = 70 } = {},
): FlightOffer {
  return {
    id,
    journeys: [
      {
        segments: [
          {
            carrierCode: carrier,
            carrierName: carrier,
            flightNumber: `${carrier} 0100`,
            origin: 'LOS',
            destination: 'ABV',
            departsAt: departs,
            arrivesAt: departs,
            durationMinutes: duration,
          },
        ],
        durationMinutes: duration,
        stops,
      },
    ],
    cabin: 'economy',
    seatsLeft: null,
    terms: {
      fareFamily: 'Saver',
      refundable: false,
      cancellation: 'Non-refundable.',
      changes: 'None.',
      checkedBaggage: '15 kg',
      cabinBaggage: '7 kg',
    },
    price: { currency: 'NGN', sellMinor: sell, margin: { netMinor: sell - 800_000, markupMinor: 800_000 } },
  };
}

function flights(overrides: Partial<FlightSearchCriteria> = {}): FlightSearchCriteria {
  return {
    tripType: 'one_way',
    legs: [{ origin: 'LOS', destination: 'ABV', date: '2026-09-18' }],
    passengers: { adults: 1, children: 0, infants: 0 },
    cabin: 'economy',
    ...overrides,
  };
}

describe('who may see the margin', () => {
  it('is Owners and Managers, never Agents', () => {
    expect(canViewMargin(['Owner'])).toBe(true);
    expect(canViewMargin(['Manager'])).toBe(true);
    expect(canViewMargin(['Agent'])).toBe(false);
    expect(canViewMargin([])).toBe(false);
  });

  it('strips the net rate and markup from every fare for someone who may not see them', () => {
    const result: SearchResult<FlightOffer> = {
      offers: [offer('a'), offer('b')],
      searchedAt: '2026-09-11T10:00:00Z',
      expiresAt: '2026-09-11T10:10:00Z',
    };

    const redacted = redactFlightResult(result, false);

    expect(redacted.offers.map((fare) => fare.price.margin)).toEqual([null, null]);
    expect(redacted.offers[0]?.price.sellMinor).toBe(10_000_000);
    // The cached original is untouched, so an Owner at the same desk still sees it.
    expect(result.offers[0]?.price.margin).not.toBeNull();
  });

  it('leaves the result alone for someone who may see them', () => {
    const result: SearchResult<FlightOffer> = {
      offers: [offer('a')],
      searchedAt: '2026-09-11T10:00:00Z',
      expiresAt: '2026-09-11T10:10:00Z',
    };

    expect(redactFlightResult(result, true)).toBe(result);
  });

  it('strips the return departures of a bus search too, not only the outbound', () => {
    const bus = {
      id: 'b',
      operator: 'GIG Mobility',
      vehicle: 'Hiace',
      departureTerminal: { id: 'x', city: 'Lagos', name: 'Jibowu' },
      arrivalTerminal: { id: 'y', city: 'Abuja', name: 'Utako' },
      departsAt: '2026-09-14T06:00',
      arrivesAt: '2026-09-14T17:00',
      durationMinutes: 660,
      availableSeats: 4,
      amenities: [],
      terms: { cancellation: '', luggage: '' },
      price: { currency: 'NGN', sellMinor: 3_000_000, margin: { netMinor: 2_700_000, markupMinor: 300_000 } },
    };

    const redacted = redactBusResult(
      { offers: [bus], returnOffers: [bus], searchedAt: '', expiresAt: '' },
      false,
    );

    expect(redacted.offers[0]?.price.margin).toBeNull();
    expect(redacted.returnOffers?.[0]?.price.margin).toBeNull();
  });
});

describe('checking a flight search before it is sent', () => {
  it('accepts a sensible search', () => {
    expect(validateFlightCriteria(flights(), TODAY)).toEqual({});
  });

  it('refuses the same airport at both ends', () => {
    const problems = validateFlightCriteria(
      flights({ legs: [{ origin: 'LOS', destination: 'LOS', date: '2026-09-18' }] }),
      TODAY,
    );

    expect(problems['leg-0-destination']).toMatch(/other than the departure/);
  });

  it('refuses a date that has passed', () => {
    const problems = validateFlightCriteria(
      flights({ legs: [{ origin: 'LOS', destination: 'ABV', date: '2026-09-10' }] }),
      TODAY,
    );

    expect(problems['leg-0-date']).toMatch(/passed/);
  });

  it('refuses a return before the outbound flight', () => {
    const problems = validateFlightCriteria(
      flights({
        tripType: 'round_trip',
        legs: [
          { origin: 'LOS', destination: 'ABV', date: '2026-09-18' },
          { origin: 'ABV', destination: 'LOS', date: '2026-09-15' },
        ],
      }),
      TODAY,
    );

    expect(problems['leg-1-date']).toMatch(/return cannot be before/);
  });

  it('refuses more infants than adults, because each one sits on an adult', () => {
    const problems = validateFlightCriteria(flights({ passengers: { adults: 1, children: 0, infants: 2 } }), TODAY);

    expect(problems['passengers']).toMatch(/infant/);
  });

  it('refuses more than nine seated passengers in one booking', () => {
    const problems = validateFlightCriteria(flights({ passengers: { adults: 6, children: 4, infants: 0 } }), TODAY);

    expect(problems['passengers']).toMatch(/up to 9/);
  });
});

describe('checking a bus search before it is sent', () => {
  function bus(overrides: Partial<BusSearchCriteria> = {}): BusSearchCriteria {
    return {
      tripType: 'one_way',
      departureTerminalId: 'trm_lag_jibowu',
      arrivalTerminalId: 'trm_abj_utako',
      date: '2026-09-14',
      returnDate: null,
      passengers: 2,
      ...overrides,
    };
  }

  it('accepts a sensible search', () => {
    expect(validateBusCriteria(bus(), TODAY, BUS_TERMINALS)).toEqual({});
  });

  it('refuses two terminals in the same city', () => {
    const problems = validateBusCriteria(bus({ arrivalTerminalId: 'trm_lag_ajah' }), TODAY, BUS_TERMINALS);

    expect(problems['to']).toMatch(/outside Lagos/);
  });

  it('needs a return date for a return trip', () => {
    const problems = validateBusCriteria(bus({ tripType: 'round_trip' }), TODAY, BUS_TERMINALS);

    expect(problems['returnDate']).toBeDefined();
  });
});

describe('filtering and sorting fares', () => {
  const fares = [
    offer('cheap-connecting', { sell: 8_000_000, stops: 1, carrier: 'ET', departs: '2026-09-14T21:00', duration: 400 }),
    offer('dear-direct', { sell: 15_000_000, stops: 0, carrier: 'P4', departs: '2026-09-14T07:00', duration: 70 }),
    offer('middle-direct', { sell: 11_000_000, stops: 0, carrier: 'QI', departs: '2026-09-14T13:30', duration: 65 }),
  ];

  it('keeps only direct flights when asked', () => {
    const kept = applyFlightFilters(fares, { ...NO_FLIGHT_FILTERS, stops: 'nonstop' });

    expect(kept.map((fare) => fare.id)).toEqual(['dear-direct', 'middle-direct']);
  });

  it('keeps only the chosen airlines', () => {
    const kept = applyFlightFilters(fares, { ...NO_FLIGHT_FILTERS, carriers: ['QI'] });

    expect(kept.map((fare) => fare.id)).toEqual(['middle-direct']);
  });

  it('drops anything above the price cap', () => {
    const kept = applyFlightFilters(fares, { ...NO_FLIGHT_FILTERS, maxPriceMinor: 11_000_000 });

    expect(kept.map((fare) => fare.id)).toEqual(['cheap-connecting', 'middle-direct']);
  });

  it('keeps only the chosen departure times', () => {
    const kept = applyFlightFilters(fares, { ...NO_FLIGHT_FILTERS, departureTimes: ['morning'] });

    expect(kept.map((fare) => fare.id)).toEqual(['dear-direct']);
  });

  it('sorts cheapest, fastest and earliest first', () => {
    expect(sortFlights(fares, 'cheapest')[0]?.id).toBe('cheap-connecting');
    expect(sortFlights(fares, 'fastest')[0]?.id).toBe('middle-direct');
    expect(sortFlights(fares, 'earliest')[0]?.id).toBe('dear-direct');
    expect(sortFlights(fares, 'latest')[0]?.id).toBe('cheap-connecting');
  });

  it('sorts a copy and leaves the list it was given alone', () => {
    const before = fares.map((fare) => fare.id);
    sortFlights(fares, 'fastest');

    expect(fares.map((fare) => fare.id)).toEqual(before);
  });
});

describe('finding an airport', () => {
  it('finds by exact code, by city, and by a word of the airport name', () => {
    expect(matchAirports('los', AIRPORTS)[0]?.code).toBe('LOS');
    expect(matchAirports('lag', AIRPORTS)[0]?.code).toBe('LOS');
    expect(matchAirports('abuja', AIRPORTS)[0]?.code).toBe('ABV');
    expect(matchAirports('murtala', AIRPORTS)[0]?.code).toBe('LOS');
  });

  it('offers every airport in a city', () => {
    expect(matchAirports('london', AIRPORTS).map((airport) => airport.code)).toEqual(['LHR', 'LGW']);
  });

  it('suggests the busiest airports before anything is typed', () => {
    expect(matchAirports('', AIRPORTS, 2).map((airport) => airport.code)).toEqual(['LOS', 'ABV']);
  });
});

describe('dates, times and words', () => {
  it('adds days across a month and a year', () => {
    expect(addDays('2026-09-11', 7)).toBe('2026-09-18');
    expect(addDays('2026-12-30', 3)).toBe('2027-01-02');
  });

  it('marks an overnight arrival as the next day', () => {
    expect(dayOffset('2026-09-14T22:10', '2026-09-15T06:05')).toBe(1);
    expect(dayOffset('2026-09-14T07:00', '2026-09-14T08:10')).toBe(0);
  });

  it('puts each hour in the right part of the day', () => {
    expect(timeOfDay('2026-09-14T05:59')).toBe('early');
    expect(timeOfDay('2026-09-14T06:00')).toBe('morning');
    expect(timeOfDay('2026-09-14T12:00')).toBe('afternoon');
    expect(timeOfDay('2026-09-14T18:00')).toBe('evening');
  });

  it('writes durations the way a ticket does', () => {
    expect(formatDuration(65)).toBe('1h 05m');
    expect(formatDuration(45)).toBe('45m');
    expect(formatDuration(720)).toBe('12h 00m');
  });

  it('describes the party in words', () => {
    expect(describePassengers({ adults: 2, children: 1, infants: 0 })).toBe('2 adults, 1 child');
    expect(describePassengers({ adults: 1, children: 0, infants: 1 })).toBe('1 adult, 1 infant');
  });

  it('never counts below zero once the fares have expired', () => {
    expect(secondsUntil('2026-09-11T10:00:00Z', Date.parse('2026-09-11T10:05:00Z'))).toBe(0);
    expect(secondsUntil('2026-09-11T10:10:00Z', Date.parse('2026-09-11T10:00:00Z'))).toBe(600);
  });
});
