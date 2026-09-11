import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { SEED, isoDate, resetCatalogStore } from '../../catalog/mock/catalog-store';
import { resetDepartureStore } from '../mock/departure-store';
import { mockDeparturesApi as api } from '../mock/mock-departures-api';
import type { DepartureRequest } from '../types';

/** The stand-in keeps the contract's server rules; these tests hold it to them. */

beforeEach(() => {
  resetCatalogStore();
  resetDepartureStore();
  vi.useFakeTimers({ toFake: ['setTimeout'] });
});

afterEach(() => {
  vi.useRealTimers();
});

async function settle<T>(promise: Promise<T>): Promise<T> {
  await vi.runAllTimersAsync();
  return promise;
}

async function refused(promise: Promise<unknown>, status: number): Promise<void> {
  const assertion = expect(promise).rejects.toMatchObject({ status });
  await vi.runAllTimersAsync();
  await assertion;
}

const terms: DepartureRequest = {
  departureDate: isoDate(90),
  isGroupDeparture: true,
  minPax: 4,
  capacityTotal: 12,
  cutoffDaysBefore: 14,
  depositType: 'None',
  depositPercentBasisPoints: null,
  depositAmountMinor: null,
  priceTiers: [{ minPax: 1, maxPax: null, pricePerPaxMinor: 38_500_000 }],
  installments: [],
};

describe('the departures stand-in', () => {
  it('works each status out from the seats', async () => {
    const all = await settle(api.listDepartures({}));

    expect(all.map((departure) => departure.status).sort()).toEqual([
      'Closed',
      'Guaranteed',
      'NearlyFull',
      'Open',
      'SoldOut',
    ]);
    // Still waiting: two in the queue and one offered a seat. Converted and expired entries are not counted.
    expect(all.find((departure) => departure.status === 'SoldOut')?.waitlistCount).toBe(3);
  });

  it('lists one product’s departures, soonest first', async () => {
    const zanzibar = await settle(api.listDepartures({ productId: SEED.zanzibar }));

    expect(zanzibar).toHaveLength(3);
    expect(zanzibar.map((departure) => departure.departureDate)).toEqual(
      [...zanzibar.map((departure) => departure.departureDate)].sort(),
    );
  });

  it('creates a departure for a tour, and never for a visa', async () => {
    const created = await settle(api.createDeparture(SEED.obudu, terms));
    expect(created).toMatchObject({ status: 'Open', seatsLeft: 12, version: 1 });

    await refused(api.createDeparture(SEED.dubaiVisa, terms), 422);
  });

  it('refuses a save made from a stale copy', async () => {
    const [first] = await settle(api.listDepartures({ productId: SEED.zanzibar }));
    if (!first) throw new Error('seed missing');

    await refused(
      api.saveDeparture(first.id, { ...first, capacityTotal: 18 }, first.version - 1),
      409,
    );
  });

  it('never lets the capacity drop below the seats already taken', async () => {
    const [first] = await settle(api.listDepartures({ productId: SEED.zanzibar }));
    if (!first) throw new Error('seed missing');

    await refused(api.saveDeparture(first.id, { ...first, capacityTotal: 5 }, first.version), 409);
  });

  it('closes and reopens a departure, and cancels one travellers have paid for', async () => {
    const zanzibar = await settle(api.listDepartures({ productId: SEED.zanzibar }));
    const guaranteed = zanzibar.find((departure) => departure.status === 'Guaranteed');
    if (!guaranteed) throw new Error('seed missing');

    const closed = await settle(api.act(guaranteed.id, 'close'));
    expect(closed.status).toBe('Closed');

    // Reopened, it goes back to the status its seats give it.
    const reopened = await settle(api.act(guaranteed.id, 'reopen'));
    expect(reopened.status).toBe('Guaranteed');

    // Decision 12: paid travellers are refunded through the resolution queue, so it can be cancelled.
    const cancelled = await settle(api.act(guaranteed.id, 'cancel'));
    expect(cancelled.status).toBe('Cancelled');
  });

  it('cancels a departure nobody has paid for', async () => {
    const created = await settle(api.createDeparture(SEED.obudu, terms));
    const cancelled = await settle(api.act(created.id, 'cancel'));

    expect(cancelled.status).toBe('Cancelled');
  });
});
