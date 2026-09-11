import { createApiClient } from '@trips/api-client';
import { describe, expect, it } from 'vitest';
import { ApiError } from '../../../api/errors';
import { createHttpBookingFlowApi } from '../http-booking-flow-api';
import type { BookingDraft, PriceConfirmation, TravellerDetails } from '../types';

function fakeServer(respond: (path: string) => Response) {
  const requests: Array<{ method: string; path: string; body: unknown }> = [];

  const api = createApiClient({
    baseUrl: 'http://api.test',
    fetch: async (input: RequestInfo | URL, init?: RequestInit) => {
      const request = input instanceof Request ? input : new Request(input, init);
      const text = await request.clone().text();
      const path = new URL(request.url).pathname;
      requests.push({ method: request.method, path, body: text ? JSON.parse(text) : undefined });
      return respond(path);
    },
  });

  return { api, requests };
}

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

const draft: BookingDraft = {
  product: 'flight',
  passengers: { adults: 1, children: 0, infants: 0 },
  offer: {
    id: 'offer-1',
    journeys: [],
    cabin: 'economy',
    seatsLeft: null,
    terms: {
      fareFamily: 'Standard',
      refundable: false,
      cancellation: '',
      changes: '',
      checkedBaggage: '',
      cabinBaggage: '',
    },
    price: { currency: 'NGN', sellMinor: 110_750, margin: null },
  },
};

const traveller: TravellerDetails = {
  type: 'ADT',
  title: 'Mrs',
  firstName: ' Ngozi ',
  lastName: 'Adeyemi',
  dateOfBirth: '',
  gender: '',
  email: 'ngozi@example.test',
  phone: '',
  passportNumber: '',
  passportExpiry: '',
  nationality: '',
};

const confirmed: PriceConfirmation = {
  reference: 'ORD-2026-000042',
  sellMinor: 110_750,
  searchedSellMinor: 110_750,
  currency: 'NGN',
  ticketTimeLimit: '2026-09-11T09:45:00Z',
};

describe('the booking flow over HTTP', () => {
  it('confirms the searched offer for the travellers, sending blanks as not given', async () => {
    const server = fakeServer(() =>
      json({
        reference: 'ORD-2026-000042',
        sellMinor: '110750',
        searchedSellMinor: 110750,
        currency: 'NGN',
        ticketTimeLimit: '2026-09-11T09:45:00Z',
      }),
    );

    const confirmation = await createHttpBookingFlowApi(server).confirmPrice(draft, [traveller]);

    expect(server.requests).toEqual([
      {
        method: 'POST',
        path: '/api/v1/bookings/price-confirmations',
        body: {
          offerId: 'offer-1',
          travellers: [
            expect.objectContaining({
              type: 'ADT',
              firstName: 'Ngozi',
              email: 'ngozi@example.test',
              dateOfBirth: null,
              passportNumber: null,
            }),
          ],
        },
      },
    ]);
    expect(confirmation).toEqual({ ...confirmed });
  });

  it('pays for the confirmed booking by its reference, with the idempotency key', async () => {
    const server = fakeServer(() => json({ reference: 'ORD-2026-000042' }));

    const placed = await createHttpBookingFlowApi(server).placeBooking({
      draft,
      travellers: [traveller],
      payment: 'wallet',
      confirmation: confirmed,
      idempotencyKey: 'attempt-1',
    });

    expect(placed).toEqual({ reference: 'ORD-2026-000042' });
    expect(server.requests[0]).toEqual({
      method: 'POST',
      path: '/api/v1/bookings',
      body: {
        reference: 'ORD-2026-000042',
        payment: 'wallet',
        acceptedSellMinor: 110_750,
        idempotencyKey: 'attempt-1',
      },
    });
  });

  it('refuses to pay for a price the server never confirmed, without asking it', async () => {
    const server = fakeServer(() => json({}));
    const { reference: _unused, ...unconfirmed } = confirmed;

    await expect(
      createHttpBookingFlowApi(server).placeBooking({
        draft,
        travellers: [traveller],
        payment: 'wallet',
        confirmation: unconfirmed,
        idempotencyKey: 'attempt-1',
      }),
    ).rejects.toBeInstanceOf(ApiError);
    expect(server.requests).toHaveLength(0);
  });

  it('reads progress, and anything it does not recognise as still waiting', async () => {
    const ticketed = fakeServer(() => json({ status: 'ticketed', pnr: 'RE6MIK' }));
    const unknown = fakeServer(() => json({ status: 'something-new', pnr: null }));

    expect(await createHttpBookingFlowApi(ticketed).getProgress('ORD-2026-000042')).toEqual({
      status: 'ticketed',
      pnr: 'RE6MIK',
    });
    expect(ticketed.requests[0]?.path).toBe('/api/v1/bookings/ORD-2026-000042/progress');
    expect(await createHttpBookingFlowApi(unknown).getProgress('ORD-2026-000042')).toEqual({
      status: 'awaiting_ticket',
      pnr: null,
    });
  });

  it("passes the server's refusal on in the agent's words", async () => {
    const server = fakeServer(() =>
      json(
        {
          title: 'There is not enough in the wallet for this booking.',
          detail: 'Nothing was booked or charged. Top up, then try again.',
        },
        422,
      ),
    );

    const refusal = await createHttpBookingFlowApi(server)
      .confirmPrice(draft, [traveller])
      .catch((error: unknown) => error);

    expect(refusal).toBeInstanceOf(ApiError);
    expect(refusal).toMatchObject({
      status: 422,
      title: 'There is not enough in the wallet for this booking.',
    });
  });
});
