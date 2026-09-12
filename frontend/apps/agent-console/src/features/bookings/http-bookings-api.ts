import { int64 } from '@trips/utils';
import type { ApiClient, Schemas } from '@trips/api-client';
import { unwrap } from '../../api/errors';
import type { BookingsApi } from './bookings-api';
import type {
  BookingDetail,
  BookingDocument,
  BookingListItem,
  BookingStatus,
  BookingTraveller,
  PaidFrom,
  ProductKind,
} from './types';

/**
 * The bookings screens against the real orders API (#42, #44) and its documents (#46).
 *
 *   listBookings    → GET  /api/v1/bookings
 *   getBooking      → GET  /api/v1/bookings/{reference}
 *   resolve         → POST /api/v1/bookings/{reference}/resolution
 *   listDocuments   → GET  /api/v1/documents?orderReference={reference}
 *   reissueDocument → POST /api/v1/documents/{documentId}/reissue
 *
 * The server leaves the margin out for anyone without `margin.view`; `useBooking` strips it again.
 * Each document's download link is signed and short-lived, so the list is asked for again rather
 * than kept.
 */
export function createHttpBookingsApi({ api }: { api: ApiClient }): BookingsApi {
  return {
    async listDocuments(reference) {
      return (
        await unwrap(
          api.GET('/api/v1/documents', { params: { query: { orderReference: reference } } }),
        )
      ).map(toDocument);
    },

    async reissueDocument(documentId) {
      return toDocument(
        await unwrap(
          api.POST('/api/v1/documents/{documentId}/reissue', { params: { path: { documentId } } }),
        ),
      );
    },

    async listBookings() {
      return (await unwrap(api.GET('/api/v1/bookings'))).map(toListItem);
    },

    async getBooking(reference) {
      return toDetail(
        await unwrap(api.GET('/api/v1/bookings/{reference}', { params: { path: { reference } } })),
      );
    },

    async resolve(reference, action) {
      return toDetail(
        await unwrap(
          api.POST('/api/v1/bookings/{reference}/resolution', {
            params: { path: { reference } },
            body: { action },
          }),
        ),
      );
    },
  };
}

type ListItemResponse = Schemas['BookingListItemResponse'];

export function toListItem(item: ListItemResponse): BookingListItem {
  return {
    reference: item.reference,
    leadTraveller: item.leadTraveller,
    travellerCount: int64(item.travellerCount),
    product: toProduct(item.product),
    origin: item.origin,
    destination: item.destination,
    carrier: item.carrier,
    departsAt: item.departsAt,
    status: toStatus(item.status),
    sellMinor: int64(item.sellMinor),
    currency: item.currency,
    ticketTimeLimit: item.ticketTimeLimit ?? null,
    pnr: item.pnr ?? null,
    bookedAt: item.bookedAt,
  };
}

export function toDetail(detail: Schemas['BookingDetailResponse']): BookingDetail {
  return {
    ...toListItem(detail),
    paidFrom: toPaidFrom(detail.paidFrom),
    travellers: detail.travellers.map((traveller) => ({
      type: toTravellerType(traveller.type),
      name: traveller.name,
      ticketNumber: traveller.ticketNumber ?? null,
    })),
    segments: detail.segments.map((segment) => ({
      carrier: segment.carrier,
      origin: segment.origin,
      destination: segment.destination,
      departsAt: segment.departsAt,
      arrivesAt: segment.arrivesAt ?? null,
    })),
    price: {
      sellMinor: int64(detail.price.sellMinor),
      margin: detail.price.margin
        ? {
            netMinor: int64(detail.price.margin.netMinor),
            markupMinor: int64(detail.price.margin.markupMinor),
          }
        : null,
    },
    timeline: detail.timeline.map((entry) => ({
      at: entry.at,
      status: toStatus(entry.status),
      note: entry.note,
    })),
    failure: detail.failure
      ? {
          reason: detail.failure.reason,
          atRiskMinor: int64(detail.failure.atRiskMinor),
          paidFrom: toPaidFrom(detail.failure.paidFrom),
        }
      : null,
  };
}

/** A document as the screens use it: its 64-bit numbers, which JSON may carry as strings, as numbers. */
export function toDocument(document: Schemas['BookingDocumentResponse']): BookingDocument {
  return {
    ...document,
    issueNumber: int64(document.issueNumber),
    sizeBytes:
      document.sizeBytes === null || document.sizeBytes === undefined
        ? null
        : int64(document.sizeBytes),
  };
}

const STATUSES: readonly string[] = [
  'awaiting_ticket',
  'confirmed',
  'ticketed',
  'failed',
  'cancelled',
];

function isStatus(value: string): value is BookingStatus {
  return STATUSES.includes(value);
}

function toStatus(value: string): BookingStatus {
  return isStatus(value) ? value : 'awaiting_ticket';
}

function toProduct(value: string): ProductKind {
  return value === 'bus' ? 'bus' : 'flight';
}

function toPaidFrom(value: string): PaidFrom {
  return value === 'card' ? 'card' : 'wallet';
}

function toTravellerType(value: string): BookingTraveller['type'] {
  return value === 'CHD' || value === 'INF' ? value : 'ADT';
}
