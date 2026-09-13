import { describe, expect, it } from 'vitest';
import { toDeparture, toManifestEntry, toWaitlistEntry } from '../http-departures-api';

/**
 * The API sends 64-bit numbers as strings when they will not fit a JSON number,
 * and enums as plain strings; the adapter turns both into the console's types
 * once, at the edge.
 */

describe('reading the departures API', () => {
  it('turns a departure response into the console’s departure, numbers and all', () => {
    const departure = toDeparture({
      id: 'd1',
      productId: 'p1',
      productTitle: 'Kilimanjaro Trek',
      currency: 'NGN',
      departureDate: '2026-09-14',
      isGroupDeparture: true,
      minPax: '6',
      capacityTotal: 20,
      cutoffDaysBefore: '14',
      depositType: 'Percent',
      depositPercentBasisPoints: '2500',
      depositAmountMinor: null,
      priceTiers: [
        { minPax: 1, maxPax: '3', pricePerPaxMinor: '139000000' },
        { minPax: '4', maxPax: null, pricePerPaxMinor: 129_000_000 },
      ],
      installments: [
        {
          sequence: '1',
          dueBasis: 'BeforeDeparture',
          dueOffsetDays: '90',
          percentOfBalanceBasisPoints: '5000',
        },
      ],
      status: 'NearlyFull',
      capacityReserved: '3',
      capacityConfirmed: 14,
      seatsLeft: '3',
      waitlistCount: '2',
      cutoffAt: '2026-08-30T23:00:00Z',
      version: '4',
    });

    expect(departure).toMatchObject({
      minPax: 6,
      cutoffDaysBefore: 14,
      depositPercentBasisPoints: 2500,
      depositAmountMinor: null,
      status: 'NearlyFull',
      capacityReserved: 3,
      seatsLeft: 3,
      waitlistCount: 2,
      version: 4,
    });

    expect(departure.priceTiers).toEqual([
      { minPax: 1, maxPax: 3, pricePerPaxMinor: 139_000_000 },
      { minPax: 4, maxPax: null, pricePerPaxMinor: 129_000_000 },
    ]);

    expect(departure.installments[0]).toEqual({
      sequence: 1,
      dueBasis: 'BeforeDeparture',
      dueOffsetDays: 90,
      percentOfBalanceBasisPoints: 5000,
    });
  });

  it('keeps a fixed deposit and drops the percentage that does not apply', () => {
    const departure = toDeparture({
      id: 'd2',
      productId: 'p1',
      productTitle: 'Obudu weekend',
      currency: 'NGN',
      departureDate: '2026-11-02',
      isGroupDeparture: false,
      minPax: 1,
      capacityTotal: 12,
      cutoffDaysBefore: 7,
      depositType: 'Fixed',
      depositPercentBasisPoints: null,
      depositAmountMinor: '5000000',
      priceTiers: [{ minPax: 1, maxPax: null, pricePerPaxMinor: '25000000' }],
      installments: [],
      status: 'Guaranteed',
      capacityReserved: 0,
      capacityConfirmed: 0,
      seatsLeft: 12,
      waitlistCount: 0,
      cutoffAt: '2026-10-25T23:00:00Z',
      version: 1,
    });

    expect(departure.depositType).toBe('Fixed');
    expect(departure.depositAmountMinor).toBe(5_000_000);
    expect(departure.depositPercentBasisPoints).toBeNull();
    expect(departure.installments).toEqual([]);
  });

  it('turns a manifest row and a waitlist entry into the console’s types', () => {
    expect(
      toManifestEntry({
        orderReference: 'ORD-2026-000142',
        travellerName: 'Ada Obi',
        paxType: 'Adult',
        room: 'Twin 3',
        status: 'Confirmed',
      }),
    ).toEqual({
      orderReference: 'ORD-2026-000142',
      travellerName: 'Ada Obi',
      paxType: 'Adult',
      room: 'Twin 3',
      status: 'Confirmed',
    });

    expect(
      toWaitlistEntry({
        id: 'w1',
        name: 'Bola Ade',
        paxCount: '2',
        status: 'Offered',
        joinedAt: '2026-09-01T10:00:00Z',
        offeredAt: '2026-09-03T10:00:00Z',
        expiresAt: '2026-09-05T10:00:00Z',
      }),
    ).toMatchObject({ paxCount: 2, status: 'Offered', expiresAt: '2026-09-05T10:00:00Z' });
  });
});
