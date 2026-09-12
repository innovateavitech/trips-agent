import type { ApiClient, Schemas } from '@trips/api-client';
import { ApiError, unwrap } from '../../api/errors';
import { int64 } from '../../api/int64';
import type { BookingFlowApi } from './booking-api';
import type { BookingProgress, TravellerDetails } from './types';

/**
 * The booking flow against the real checkout (#42).
 *
 *   confirmPrice  → POST /api/v1/bookings/price-confirmations  holds the wallet, then the fare
 *   placeBooking  → POST /api/v1/bookings                       the same idempotency key is the same booking
 *   getProgress   → GET  /api/v1/bookings/{reference}/progress  polled: the supplier has no webhooks
 *
 * The confirmation carries the server's `reference` for the booking it created, and paying names it.
 * Every refusal arrives as an `ApiError` whose title and detail are written for the agent.
 */
export function createHttpBookingFlowApi({ api }: { api: ApiClient }): BookingFlowApi {
  return {
    async confirmPrice(draft, travellers) {
      const confirmation = await unwrap(
        api.POST('/api/v1/bookings/price-confirmations', {
          body: { offerId: draft.offer.id, travellers: travellers.map(toTravellerRequest) },
        }),
      );

      return {
        reference: confirmation.reference,
        sellMinor: int64(confirmation.sellMinor),
        searchedSellMinor: int64(confirmation.searchedSellMinor),
        currency: confirmation.currency,
        ticketTimeLimit: confirmation.ticketTimeLimit,
      };
    },

    async placeBooking(order) {
      const reference = order.confirmation.reference;

      if (!reference) {
        // A price the server never confirmed has nothing behind it to pay for.
        throw new ApiError(
          409,
          'Confirm the price again.',
          'This price was not confirmed with the supplier, so there is nothing to pay for yet.',
        );
      }

      return unwrap(
        api.POST('/api/v1/bookings', {
          body: {
            reference,
            payment: order.payment,
            acceptedSellMinor: order.confirmation.sellMinor,
            idempotencyKey: order.idempotencyKey,
          },
        }),
      );
    },

    async getProgress(reference) {
      const progress = await unwrap(
        api.GET('/api/v1/bookings/{reference}/progress', { params: { path: { reference } } }),
      );

      return { status: toProgressStatus(progress.status), pnr: progress.pnr ?? null };
    },
  };
}

/** A traveller as the API takes them: what the form left blank is "not given", not an empty string. */
export function toTravellerRequest(
  traveller: TravellerDetails,
): Schemas['BookingTravellerRequest'] {
  return {
    type: traveller.type,
    title: given(traveller.title),
    firstName: traveller.firstName.trim(),
    lastName: traveller.lastName.trim(),
    dateOfBirth: given(traveller.dateOfBirth),
    gender: given(traveller.gender),
    email: given(traveller.email),
    phone: given(traveller.phone),
    passportNumber: given(traveller.passportNumber),
    passportExpiry: given(traveller.passportExpiry),
    nationality: given(traveller.nationality),
  };
}

function given(value: string): string | null {
  const trimmed = value.trim();
  return trimmed === '' ? null : trimmed;
}

function toProgressStatus(status: string): BookingProgress['status'] {
  return status === 'ticketed' || status === 'failed' ? status : 'awaiting_ticket';
}
