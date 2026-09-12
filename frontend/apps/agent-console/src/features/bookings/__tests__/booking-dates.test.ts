import { describe, expect, it } from 'vitest';
import { dateRangeProblem, filterBookings, lagosDay } from '../bookings-rules';
import { NO_BOOKING_FILTERS, type BookingListItem } from '../types';

/** Issue 54's date filter: travel date or booking date, by the day in Lagos. */

function booking(reference: string, departsAt: string, bookedAt: string): BookingListItem {
  return {
    reference,
    leadTraveller: 'Adaeze Okafor',
    travellerCount: 1,
    product: 'flight',
    origin: 'LOS',
    destination: 'ABV',
    carrier: 'Air Peace P4 7121',
    departsAt,
    status: 'ticketed',
    sellMinor: 14_250_000,
    currency: 'NGN',
    ticketTimeLimit: null,
    pnr: 'QX7K2P',
    bookedAt,
  };
}

const bookings = [
  // Leaves at 00:30 on 2 October in Lagos, though it is still the 1st in UTC.
  booking('TRP-LATE01', '2026-10-01T23:30:00Z', '2026-09-20T09:00:00Z'),
  booking('TRP-OCT05', '2026-10-05T08:00:00Z', '2026-09-28T09:00:00Z'),
  booking('TRP-OCT09', '2026-10-09T08:00:00Z', '2026-10-01T09:00:00Z'),
];

const references = (list: readonly BookingListItem[]) => list.map((item) => item.reference);

describe('filtering bookings by date', () => {
  it('reads the day in Lagos, not in UTC or in the browser', () => {
    expect(lagosDay('2026-10-01T23:30:00Z')).toBe('2026-10-02');
    expect(lagosDay('2026-10-01T22:59:00Z')).toBe('2026-10-01');
  });

  it('keeps departures on and between the chosen days, both ends included', () => {
    const found = filterBookings(bookings, {
      ...NO_BOOKING_FILTERS,
      from: '2026-10-02',
      to: '2026-10-05',
    });

    expect(references(found)).toEqual(['TRP-LATE01', 'TRP-OCT05']);
  });

  it('filters by the day it was booked when asked to', () => {
    const found = filterBookings(bookings, {
      ...NO_BOOKING_FILTERS,
      dateField: 'booked',
      from: '2026-09-25',
    });

    expect(references(found)).toEqual(['TRP-OCT05', 'TRP-OCT09']);
  });

  it('leaves either end open when it is blank', () => {
    expect(
      references(filterBookings(bookings, { ...NO_BOOKING_FILTERS, to: '2026-10-04' })),
    ).toEqual(['TRP-LATE01']);
    expect(references(filterBookings(bookings, NO_BOOKING_FILTERS))).toHaveLength(3);
  });

  it('works alongside the other filters rather than instead of them', () => {
    const found = filterBookings(bookings, {
      ...NO_BOOKING_FILTERS,
      from: '2026-10-02',
      query: 'oct09',
    });

    expect(references(found)).toEqual(['TRP-OCT09']);
  });

  it('says so when the range is back to front', () => {
    expect(dateRangeProblem({ from: '2026-10-05', to: '2026-10-01' })).toMatch(/on or after/);
    expect(dateRangeProblem({ from: '2026-10-01', to: '2026-10-01' })).toBeNull();
    expect(dateRangeProblem({ from: '', to: '2026-10-01' })).toBeNull();
  });
});
