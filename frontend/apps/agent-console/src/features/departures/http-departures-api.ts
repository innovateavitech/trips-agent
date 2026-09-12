import type { Schemas } from '@trips/api-client';
import { api } from '../../api/client';
import { unwrap } from '../../api/errors';
import { toOptionalWholeNumber, toWholeNumber } from '../pricing/pricing-rules';
import type { DeparturesApi } from './departures-api';
import type {
  Departure,
  DepartureRequest,
  DepartureStatus,
  DepositType,
  DueBasis,
  ManifestEntry,
  WaitlistEntry,
} from './types';

/**
 * The departures port over the real API (build plan F6, issue 57). The screens
 * never know which adapter they have: `main.tsx` chooses, and the stand-in in
 * `mock/` stays only for demo mode.
 *
 * The API sends 64-bit numbers as `number | string` and enums as plain strings;
 * everything is turned into the console's types here, once.
 */

export function toDeparture(raw: Schemas['DepartureResponse']): Departure {
  return {
    id: raw.id,
    productId: raw.productId,
    productTitle: raw.productTitle,
    currency: raw.currency,
    departureDate: raw.departureDate,
    isGroupDeparture: raw.isGroupDeparture,
    minPax: toWholeNumber(raw.minPax),
    capacityTotal: toWholeNumber(raw.capacityTotal),
    cutoffDaysBefore: toWholeNumber(raw.cutoffDaysBefore),
    depositType: raw.depositType as DepositType,
    depositPercentBasisPoints: toOptionalWholeNumber(raw.depositPercentBasisPoints),
    depositAmountMinor: toOptionalWholeNumber(raw.depositAmountMinor),
    priceTiers: raw.priceTiers.map((tier) => ({
      minPax: toWholeNumber(tier.minPax),
      maxPax: toOptionalWholeNumber(tier.maxPax),
      pricePerPaxMinor: toWholeNumber(tier.pricePerPaxMinor),
    })),
    installments: raw.installments.map((item) => ({
      sequence: toWholeNumber(item.sequence),
      dueBasis: item.dueBasis as DueBasis,
      dueOffsetDays: toWholeNumber(item.dueOffsetDays),
      percentOfBalanceBasisPoints: toWholeNumber(item.percentOfBalanceBasisPoints),
    })),
    status: raw.status as DepartureStatus,
    capacityReserved: toWholeNumber(raw.capacityReserved),
    capacityConfirmed: toWholeNumber(raw.capacityConfirmed),
    seatsLeft: toWholeNumber(raw.seatsLeft),
    waitlistCount: toWholeNumber(raw.waitlistCount),
    cutoffAt: raw.cutoffAt,
    version: toWholeNumber(raw.version),
  };
}

export function toManifestEntry(raw: Schemas['ManifestEntryResponse']): ManifestEntry {
  return {
    orderReference: raw.orderReference,
    travellerName: raw.travellerName,
    paxType: raw.paxType as ManifestEntry['paxType'],
    room: raw.room,
    status: raw.status as ManifestEntry['status'],
  };
}

export function toWaitlistEntry(raw: Schemas['WaitlistEntryResponse']): WaitlistEntry {
  return {
    id: raw.id,
    name: raw.name,
    paxCount: toWholeNumber(raw.paxCount),
    status: raw.status as WaitlistEntry['status'],
    joinedAt: raw.joinedAt,
    offeredAt: raw.offeredAt,
    expiresAt: raw.expiresAt,
  };
}

/**
 * The body the API takes. A create ignores the version; a save is refused when
 * it is stale, which is how two people editing at once stop being a silent
 * overwrite and start being a 409 the editor can explain.
 */
function toBody(request: DepartureRequest, version: number) {
  return { ...request, version };
}

const byId = (departureId: string) => ({ params: { path: { departureId } } });

export const httpDeparturesApi: DeparturesApi = {
  async listDepartures(filter) {
    const query = filter.productId ? { productId: filter.productId } : {};

    return (await unwrap(api.GET('/api/v1/catalog/departures', { params: { query } }))).map(
      toDeparture,
    );
  },

  async getDeparture(id) {
    return toDeparture(await unwrap(api.GET('/api/v1/catalog/departures/{departureId}', byId(id))));
  },

  async createDeparture(productId, request) {
    return toDeparture(
      await unwrap(
        api.POST('/api/v1/catalog/products/{productId}/departures', {
          params: { path: { productId } },
          body: toBody(request, 0),
        }),
      ),
    );
  },

  async saveDeparture(id, request, version) {
    return toDeparture(
      await unwrap(
        api.PUT('/api/v1/catalog/departures/{departureId}', {
          ...byId(id),
          body: toBody(request, version),
        }),
      ),
    );
  },

  async act(id, action) {
    const raw =
      action === 'close'
        ? await unwrap(api.POST('/api/v1/catalog/departures/{departureId}/close', byId(id)))
        : action === 'reopen'
          ? await unwrap(api.POST('/api/v1/catalog/departures/{departureId}/reopen', byId(id)))
          : await unwrap(api.POST('/api/v1/catalog/departures/{departureId}/cancel', byId(id)));
    return toDeparture(raw);
  },

  async getManifest(id) {
    return (
      await unwrap(api.GET('/api/v1/catalog/departures/{departureId}/manifest', byId(id)))
    ).map(toManifestEntry);
  },

  async getWaitlist(id) {
    return (
      await unwrap(api.GET('/api/v1/catalog/departures/{departureId}/waitlist', byId(id)))
    ).map(toWaitlistEntry);
  },
};
