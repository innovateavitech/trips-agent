import { createApiClient } from '@trips/api-client';
import { describe, expect, it } from 'vitest';
import { createHttpBookingsApi } from '../http-bookings-api';

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

const listed = {
  reference: 'ORD-2026-000042',
  leadTraveller: 'Ngozi Adeyemi',
  travellerCount: '1',
  product: 'flight',
  origin: 'LOS',
  destination: 'LHR',
  carrier: 'P4 7121',
  departsAt: '2026-09-14T09:00:00Z',
  status: 'failed',
  sellMinor: '110750',
  currency: 'NGN',
  ticketTimeLimit: null,
  pnr: null,
  bookedAt: '2026-09-11T09:01:00Z',
};

const detail = {
  ...listed,
  paidFrom: 'wallet',
  travellers: [{ type: 'ADT', name: 'Ngozi Adeyemi', ticketNumber: null }],
  segments: [
    {
      carrier: 'P4 7121',
      origin: 'LOS',
      destination: 'LHR',
      departsAt: '2026-09-14T10:00',
      arrivesAt: null,
    },
  ],
  price: { sellMinor: 110750, margin: null },
  timeline: [
    { at: '2026-09-11T09:01:00Z', status: 'failed', note: 'The supplier reported status 0.' },
  ],
  failure: { reason: 'The supplier reported status 0.', atRiskMinor: 110750, paidFrom: 'wallet' },
};

describe('the bookings screens over HTTP', () => {
  it('lists bookings, reading amounts written either way', async () => {
    const server = fakeServer(() => json([listed]));

    const bookings = await createHttpBookingsApi(server).listBookings();

    expect(server.requests[0]).toMatchObject({ method: 'GET', path: '/api/v1/bookings' });
    expect(bookings).toEqual([{ ...listed, travellerCount: 1, sellMinor: 110_750 }]);
  });

  it('opens one booking, with its failure and without a margin it was not given', async () => {
    const server = fakeServer(() => json(detail));

    const booking = await createHttpBookingsApi(server).getBooking('ORD-2026-000042');

    expect(server.requests[0]?.path).toBe('/api/v1/bookings/ORD-2026-000042');
    expect(booking.price).toEqual({ sellMinor: 110_750, margin: null });
    expect(booking.failure).toEqual({
      reason: 'The supplier reported status 0.',
      atRiskMinor: 110_750,
      paidFrom: 'wallet',
    });
    expect(booking.travellers).toEqual([
      { type: 'ADT', name: 'Ngozi Adeyemi', ticketNumber: null },
    ]);
  });

  it("sends the agent's decision to the booking's resolution", async () => {
    const server = fakeServer(() => json({ ...detail, status: 'cancelled', failure: null }));

    const resolved = await createHttpBookingsApi(server).resolve('ORD-2026-000042', 'refund');

    expect(server.requests[0]).toEqual({
      method: 'POST',
      path: '/api/v1/bookings/ORD-2026-000042/resolution',
      body: { action: 'refund' },
    });
    expect(resolved.status).toBe('cancelled');
  });
});
