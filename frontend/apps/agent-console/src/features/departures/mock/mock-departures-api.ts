import { ApiError } from '../../../api/errors';
import { findProduct } from '../../catalog/mock/catalog-store';
import { validateDeparture } from '../departure-rules';
import type { DeparturesApi } from '../departures-api';
import type { Departure, DepartureRequest } from '../types';
import {
  allDepartures,
  findDeparture,
  productTitleOf,
  putDeparture,
  statusOf,
  type StoredDeparture,
} from './departure-store';

/**
 * ============================================================================
 *  TEMPORARY. Delete this folder when the departures API lands.
 * ============================================================================
 *
 * The departure screens' stand-in. It keeps the contract's server rules:
 * versions guard against two people overwriting each other, capacity never
 * drops below seats already taken, and a departure with confirmed travellers
 * cannot be cancelled until open question 12 says what happens to deposits.
 */

const LATENCY_MS = 350;
const SAVE_LATENCY_MS = 600;

const delay = (ms = LATENCY_MS) => new Promise((resolve) => setTimeout(resolve, ms));

/** Lagos is UTC+1 all year: bookings close at midnight Lagos time on the cutoff day. */
function cutoffAt(departureDate: string, cutoffDaysBefore: number): string {
  const departure = new Date(`${departureDate}T00:00:00+01:00`);
  return new Date(departure.getTime() - cutoffDaysBefore * 86_400_000).toISOString();
}

function toDeparture(stored: StoredDeparture): Departure {
  const { manualState: _manualState, manifest: _manifest, waitlist, ...request } = stored;
  const taken = stored.capacityConfirmed + stored.capacityReserved;

  return structuredClone({
    ...request,
    productTitle: productTitleOf(stored.productId),
    currency: findProduct(stored.productId)?.currency ?? 'NGN',
    status: statusOf(stored),
    seatsLeft: Math.max(0, stored.capacityTotal - taken),
    waitlistCount: waitlist.filter(
      (entry) => entry.status === 'Waiting' || entry.status === 'Offered',
    ).length,
    cutoffAt: cutoffAt(stored.departureDate, stored.cutoffDaysBefore),
  });
}

function mustFind(id: string): StoredDeparture {
  const departure = findDeparture(id);
  if (!departure) {
    throw new ApiError(
      404,
      'We could not find that departure.',
      'It may belong to another agency.',
    );
  }
  return departure;
}

/** The contract's request rules, as the server applies them: every problem, not just the first. */
function refuseInvalid(request: DepartureRequest): void {
  const problems = Object.values(validateDeparture(request, new Date().toISOString().slice(0, 10)));
  if (problems.length > 0)
    throw new ApiError(422, 'This departure is not valid.', problems.join(' '));
}

export const mockDeparturesApi: DeparturesApi = {
  async listDepartures({ productId }) {
    await delay();
    return allDepartures()
      .filter((departure) => !productId || departure.productId === productId)
      .sort((a, b) => a.departureDate.localeCompare(b.departureDate))
      .map(toDeparture);
  },

  async getDeparture(id) {
    await delay();
    return toDeparture(mustFind(id));
  },

  async createDeparture(productId, request) {
    await delay(SAVE_LATENCY_MS);
    const product = findProduct(productId);

    if (!product) throw new ApiError(404, 'We could not find that product.');
    if (product.productType === 'Visa') {
      throw new ApiError(
        422,
        'Visas have no departures.',
        'Departures are for tours and packages.',
      );
    }
    refuseInvalid(request);

    const departure: StoredDeparture = {
      ...structuredClone(request),
      id: crypto.randomUUID(),
      productId,
      capacityReserved: 0,
      capacityConfirmed: 0,
      manualState: null,
      version: 1,
      manifest: [],
      waitlist: [],
    };
    putDeparture(departure);
    return toDeparture(departure);
  },

  async saveDeparture(id, request, version) {
    await delay(SAVE_LATENCY_MS);
    const existing = mustFind(id);

    if (existing.version !== version) {
      throw new ApiError(
        409,
        'Someone else changed this departure.',
        'Reload it to see their changes, then make yours again.',
      );
    }
    if (existing.manualState === 'Cancelled') {
      throw new ApiError(409, 'A cancelled departure cannot be changed.');
    }
    const taken = existing.capacityConfirmed + existing.capacityReserved;
    if (request.capacityTotal < taken) {
      throw new ApiError(
        409,
        `${taken} seats are already taken.`,
        `The capacity cannot go below ${taken}.`,
      );
    }
    refuseInvalid(request);

    const next: StoredDeparture = {
      ...existing,
      ...structuredClone(request),
      version: existing.version + 1,
    };
    putDeparture(next);
    return toDeparture(next);
  },

  async act(id, action) {
    await delay(SAVE_LATENCY_MS);
    const departure = mustFind(id);

    if (departure.manualState === 'Cancelled') {
      throw new ApiError(409, 'This departure is cancelled.');
    }

    if (action === 'close') {
      if (departure.manualState === 'Closed')
        throw new ApiError(409, 'This departure is already closed.');
      putDeparture({ ...departure, manualState: 'Closed', version: departure.version + 1 });
    } else if (action === 'reopen') {
      if (departure.manualState !== 'Closed')
        throw new ApiError(409, 'This departure is not closed.');
      putDeparture({ ...departure, manualState: null, version: departure.version + 1 });
    } else {
      if (departure.capacityConfirmed > 0) {
        throw new ApiError(
          409,
          `${departure.capacityConfirmed} travellers have paid for this departure.`,
          'What happens to their deposits is still an open question with the client (question 12), so it cannot be cancelled here yet. Close it to stop new bookings.',
        );
      }
      putDeparture({ ...departure, manualState: 'Cancelled', version: departure.version + 1 });
    }

    return toDeparture(mustFind(id));
  },

  async getManifest(id) {
    await delay();
    return structuredClone(mustFind(id).manifest);
  },

  async getWaitlist(id) {
    await delay();
    return structuredClone(mustFind(id).waitlist);
  },
};
