import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { resetCrmStore } from '../mock/crm-store';
import { mockCrmApi as api } from '../mock/mock-crm-api';

/** The stand-in keeps the CRM contract's rules; these tests hold it to them. */

beforeEach(() => {
  resetCrmStore();
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

const lead = {
  customer: { name: 'Ngozi Adeyemi', email: null, phone: '0803 000 1122' },
  destination: 'Obudu',
  travelFrom: null,
  travelTo: null,
  adults: 2,
  children: 0,
  budgetMinMinor: null,
  budgetMaxMinor: null,
  message: 'Called about a weekend.',
};

describe('the CRM stand-in', () => {
  it('makes the customer from the lead, and finds them again by phone', async () => {
    const before = (await settle(api.listCustomers())).length;
    const first = await settle(api.createLead(lead));
    const second = await settle(
      api.createLead({ ...lead, customer: { ...lead.customer, phone: '08030001122' } }),
    );

    expect(first.stage).toBe('New');
    expect(second.customer.id).toBe(first.customer.id);
    expect(await settle(api.listCustomers())).toHaveLength(before + 1);
  });

  it('wants a reason before a lead is lost, and keeps it in the history', async () => {
    await refused(api.moveLead('l-dubai', 'Lost', ' '), 422);

    const lost = await settle(api.moveLead('l-dubai', 'Lost', 'Went with a cheaper hotel.'));
    expect(lost.lostReason).toBe('Went with a cheaper hotel.');
    expect(lost.history.at(-1)).toMatchObject({
      stage: 'Lost',
      reason: 'Went with a cheaper hotel.',
    });
  });

  it('sends a quote: the lead becomes Quoted, and the message is logged', async () => {
    const quote = await settle(
      api.createQuote('l-dubai', {
        title: 'Dubai for 2 adults',
        validUntil: new Date(Date.now() + 7 * 86_400_000).toISOString().slice(0, 10),
        items: [
          {
            description: 'Five nights, 4-star',
            quantity: 2,
            unitPriceMinor: 180_000_000,
            productId: null,
          },
        ],
        itinerary: [],
        notes: '',
      }),
    );
    expect(quote).toMatchObject({ status: 'Draft', totalMinor: 360_000_000, publicUrl: null });

    const sent = await settle(api.sendQuote(quote.id));
    expect(sent.status).toBe('Sent');
    expect(sent.publicUrl).toMatch(/^https:\/\//);

    const updated = await settle(api.getLead('l-dubai'));
    expect(updated.stage).toBe('Quoted');
    expect(updated.communications[0]?.summary).toBe(`Sent quote ${quote.quoteNumber}.`);
  });

  it('never changes a quote the customer has', async () => {
    const sent = await settle(api.getQuote('q-safari'));

    await refused(api.saveQuote(sent.id, { ...sent, title: 'Changed' }), 409);
  });

  it('marks a task done once, and only once', async () => {
    const done = await settle(api.completeTask('t-1'));
    expect(done.completedAt).not.toBeNull();

    await refused(api.completeTask('t-1'), 409);
  });
});
