import { describe, expect, it } from 'vitest';
import { toCustomer, toLead, toQuote } from '../http-crm-api';

/**
 * The API sends 64-bit numbers as strings when they will not fit a JSON number,
 * and enums as plain strings; the adapter turns both into the console's own
 * types once, so no screen has to.
 */

describe('reading the CRM API', () => {
  it('turns a lead response into the console’s lead, numbers and all', () => {
    const lead = toLead({
      id: 'l1',
      customer: { id: 'c1', name: 'Chiamaka Okonkwo', email: 'chiamaka@example.test', phone: null },
      source: 'TripRequestWidget',
      destination: 'Dubai',
      travelFrom: '2026-12-01',
      travelTo: null,
      adults: 2,
      children: '1',
      budgetMaxMinor: '50000000',
      currency: 'NGN',
      stage: 'Quoted',
      ownerName: null,
      createdAt: '2026-09-12T10:00:00Z',
      nextTaskDueAt: '2026-09-13T09:00:00Z',
      quoteCount: '2',
      message: 'Somewhere warm in March.',
      budgetMinMinor: '30000000',
      lostReason: null,
      history: [
        { stage: 'New', at: '2026-09-12T10:00:00Z', byName: 'Website', reason: null },
        { stage: 'Quoted', at: '2026-09-12T11:00:00Z', byName: 'Ada Obi', reason: null },
      ],
      quotes: [
        {
          id: 'q1',
          quoteNumber: 'QT-0001',
          title: 'Dubai, seven nights',
          status: 'Sent',
          totalMinor: '97500000',
          currency: 'NGN',
          validUntil: '2026-09-26',
          sentAt: '2026-09-12T11:00:00Z',
        },
      ],
      tasks: [],
      communications: [],
    });

    expect(lead).toMatchObject({
      children: 1,
      budgetMinMinor: 30_000_000,
      budgetMaxMinor: 50_000_000,
      quoteCount: 2,
      source: 'TripRequestWidget',
      stage: 'Quoted',
    });
    expect(lead.quotes[0].totalMinor).toBe(97_500_000);
    expect(lead.history[0].byName).toBe('Website');
  });

  it('turns a quote response into the console’s quote, with its items and days', () => {
    const quote = toQuote({
      id: 'q1',
      quoteNumber: 'QT-0001',
      leadId: 'l1',
      customer: { id: 'c1', name: 'Chiamaka Okonkwo', email: null, phone: '+234 803 000 1122' },
      title: 'Dubai, seven nights',
      status: 'Viewed',
      validUntil: '2026-09-26',
      currency: 'NGN',
      items: [
        {
          description: 'Hotel, two nights',
          quantity: '2',
          unitPriceMinor: '45000000',
          productId: null,
        },
        {
          description: 'Airport transfer',
          quantity: 1,
          unitPriceMinor: 7_500_000,
          productId: 'p1',
        },
      ],
      itinerary: [{ dayNumber: '1', title: 'Arrive', description: 'Transfer to the hotel.' }],
      notes: 'Prices hold until the date above.',
      totalMinor: '97500000',
      publicUrl: 'https://lekki-horizon.com/q/abc',
      sentAt: '2026-09-12T11:00:00Z',
      viewedAt: '2026-09-12T14:00:00Z',
      respondedAt: null,
    });

    expect(quote.totalMinor).toBe(97_500_000);
    expect(quote.items[0]).toMatchObject({ quantity: 2, unitPriceMinor: 45_000_000 });
    expect(quote.itinerary[0].dayNumber).toBe(1);
    expect(quote.publicUrl).toBe('https://lekki-horizon.com/q/abc');
  });

  it('turns a customer response into the console’s customer 360', () => {
    const customer = toCustomer({
      id: 'c1',
      name: 'Chiamaka Okonkwo',
      email: 'chiamaka@example.test',
      phone: null,
      lifetimeValueMinor: '240000000',
      totalBookings: '3',
      lastActivityAt: '2026-09-12T10:00:00Z',
      openLeadCount: 1,
      currency: 'NGN',
      createdAt: '2026-01-04T09:00:00Z',
      leads: [],
      quotes: [],
      bookings: [
        {
          reference: 'ORD-2026-000142',
          title: 'Lagos to Dubai',
          travelDate: null,
          status: 'Confirmed',
          amountMinor: '80000000',
        },
      ],
      tasks: [
        {
          id: 't1',
          title: 'Call her back about dates',
          dueAt: '2026-09-13T09:00:00Z',
          completedAt: null,
          related: { type: 'Lead', id: 'l1', label: 'Chiamaka Okonkwo · Dubai' },
          ownerName: 'Ada Obi',
        },
      ],
      communications: [
        {
          id: 'm1',
          channel: 'Whatsapp',
          direction: 'Outbound',
          summary: 'Sent her three hotel options.',
          at: '2026-09-12T12:00:00Z',
          byName: 'Ada Obi',
          related: { type: 'Lead', id: 'l1' },
        },
      ],
    });

    expect(customer).toMatchObject({
      lifetimeValueMinor: 240_000_000,
      totalBookings: 3,
      openLeadCount: 1,
    });
    expect(customer.bookings[0].amountMinor).toBe(80_000_000);
    expect(customer.tasks[0].related.label).toBe('Chiamaka Okonkwo · Dubai');
    expect(customer.communications[0].channel).toBe('Whatsapp');
  });
});
