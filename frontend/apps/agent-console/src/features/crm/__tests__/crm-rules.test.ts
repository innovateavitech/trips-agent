import { describe, expect, it } from 'vitest';
import {
  bucketOf,
  buildQuoteRequest,
  describeBudget,
  describeDates,
  describeTrip,
  draftFromQuote,
  groupByStage,
  newCrmKey,
  quoteTotalMinor,
  relativeTime,
  searchLeads,
  whyNotSendable,
  type QuoteDraft,
} from '../crm-rules';
import { buildLeadRequest, EMPTY_LEAD_FORM } from '../lead-form';
import type { LeadSummary } from '../types';

const TODAY = '2026-09-11';
const NOW = new Date('2026-09-11T10:00:00+01:00');

function lead(patch: Partial<LeadSummary>): LeadSummary {
  return {
    id: 'l1',
    customer: {
      id: 'c1',
      name: 'Adeola Martins',
      email: 'adeola@example.test',
      phone: '0809 115 3348',
    },
    source: 'TripRequestWidget',
    destination: 'Zanzibar',
    travelFrom: '2026-11-20',
    travelTo: '2026-11-27',
    adults: 2,
    children: 0,
    budgetMaxMinor: null,
    currency: 'NGN',
    stage: 'New',
    ownerName: null,
    createdAt: '2026-09-10T10:00:00Z',
    nextTaskDueAt: null,
    quoteCount: 0,
    ...patch,
  };
}

describe('the pipeline', () => {
  it('puts each lead in its stage, newest first', () => {
    const columns = groupByStage([
      lead({ id: 'a', stage: 'New', createdAt: '2026-09-01T00:00:00Z' }),
      lead({ id: 'b', stage: 'New', createdAt: '2026-09-09T00:00:00Z' }),
      lead({ id: 'c', stage: 'Won' }),
    ]);

    expect(columns.New.map((l) => l.id)).toEqual(['b', 'a']);
    expect(columns.Won.map((l) => l.id)).toEqual(['c']);
    expect(columns.Lost).toEqual([]);
  });

  it('finds a lead by name, phone or destination', () => {
    const leads = [
      lead({ id: 'a' }),
      lead({
        id: 'b',
        destination: 'Dubai',
        customer: { id: 'c2', name: 'Chiamaka Okonkwo', email: null, phone: '0803 214 5521' },
      }),
    ];

    expect(searchLeads(leads, 'dubai').map((l) => l.id)).toEqual(['b']);
    expect(searchLeads(leads, '0803').map((l) => l.id)).toEqual(['b']);
    expect(searchLeads(leads, 'martins').map((l) => l.id)).toEqual(['a']);
  });
});

describe('describing a trip', () => {
  it('writes dates the short way', () => {
    expect(describeDates('2026-11-20', '2026-11-27')).toBe('20–27 Nov');
    expect(describeDates('2026-11-28', '2026-12-03')).toBe('28 Nov – 3 Dec');
    expect(describeDates('2026-11-20', null)).toBe('20 Nov');
    expect(describeDates(null, null)).toBe('');
  });

  it('says where, when and who', () => {
    expect(describeTrip(lead({ children: 1 }))).toBe('Zanzibar · 20–27 Nov · 2 adults, 1 child');
  });

  it('says the budget however much of it was given', () => {
    expect(describeBudget(null, 450_000_000, 'NGN')).toMatch(/^Up to /);
    expect(describeBudget(300_000_000, 400_000_000, 'NGN')).toMatch(/ to /);
    expect(describeBudget(null, null, 'NGN')).toBe('No budget given');
  });

  it('says how long ago, or how long until', () => {
    expect(relativeTime('2026-09-11T08:00:00+01:00', NOW)).toBe('2 hours ago');
    expect(relativeTime('2026-09-14T10:00:00+01:00', NOW)).toBe('in 3 days');
    expect(relativeTime('2026-09-11T10:00:20+01:00', NOW)).toBe('just now');
  });
});

describe('tasks', () => {
  it('sorts a task into overdue, today, later or done, by Lagos days', () => {
    expect(bucketOf({ dueAt: '2026-09-11T09:00:00+01:00', completedAt: null }, NOW)).toBe(
      'overdue',
    );
    expect(bucketOf({ dueAt: '2026-09-11T17:00:00+01:00', completedAt: null }, NOW)).toBe('today');
    expect(bucketOf({ dueAt: '2026-09-12T09:00:00+01:00', completedAt: null }, NOW)).toBe(
      'upcoming',
    );
    expect(
      bucketOf({ dueAt: '2026-09-01T09:00:00+01:00', completedAt: '2026-09-02T09:00:00Z' }, NOW),
    ).toBe('done');
  });
});

describe('quotes', () => {
  const draft: QuoteDraft = {
    title: 'Zanzibar honeymoon',
    validUntil: '2026-09-18',
    items: [
      {
        key: newCrmKey(),
        description: 'Beach hotel, double',
        quantity: '2',
        unitPrice: '1,450,000',
      },
      { key: newCrmKey(), description: '', quantity: '1', unitPrice: '' },
    ],
    itinerary: [{ key: newCrmKey(), title: 'Arrive', description: '' }],
    notes: '',
  };

  it('builds a quote from the rows that were filled in, and adds it up', () => {
    const built = buildQuoteRequest(draft, TODAY);

    expect(built.ok).toBe(true);
    if (!built.ok) return;
    expect(built.request.items).toHaveLength(1);
    expect(quoteTotalMinor(built.request.items)).toBe(290_000_000);
    expect(built.request.itinerary).toEqual([{ dayNumber: 1, title: 'Arrive', description: '' }]);
  });

  it('names every problem at once', () => {
    const built = buildQuoteRequest(
      {
        ...draft,
        title: ' ',
        validUntil: '2026-09-01',
        items: [{ key: newCrmKey(), description: '', quantity: '0', unitPrice: 'lots' }],
      },
      TODAY,
    );

    expect(built.ok).toBe(false);
    if (built.ok) return;
    expect(Object.keys(built.errors).sort()).toEqual([
      'items.0.description',
      'items.0.quantity',
      'items.0.unitPrice',
      'title',
      'validUntil',
    ]);
  });

  it('reads a quote back into the builder unchanged', () => {
    const request = {
      title: 'Obudu',
      validUntil: '2026-09-20',
      items: [{ description: 'Chalet', quantity: 2, unitPriceMinor: 38_500_000, productId: null }],
      itinerary: [],
      notes: 'Held for a week.',
    };
    const built = buildQuoteRequest(draftFromQuote(request), TODAY);

    expect(built.ok && built.request).toEqual(request);
  });

  it('says why a quote cannot be sent', () => {
    const items = [{ description: 'Hotel', quantity: 1, unitPriceMinor: 100, productId: null }];

    expect(whyNotSendable({ status: 'Draft', items, validUntil: '2026-09-20' }, TODAY)).toBeNull();
    expect(whyNotSendable({ status: 'Sent', items, validUntil: '2026-09-20' }, TODAY)).toMatch(
      /already been sent/,
    );
    expect(whyNotSendable({ status: 'Draft', items: [], validUntil: '2026-09-20' }, TODAY)).toMatch(
      /at least one item/,
    );
    expect(whyNotSendable({ status: 'Draft', items, validUntil: '2026-09-01' }, TODAY)).toMatch(
      /expired/,
    );
  });
});

describe('the new-lead form', () => {
  it('needs a person, a way to reach them and a destination', () => {
    const built = buildLeadRequest(EMPTY_LEAD_FORM);

    expect(built.ok).toBe(false);
    if (built.ok) return;
    expect(Object.keys(built.errors).sort()).toEqual(['destination', 'email', 'name']);
  });

  it('takes a phone number instead of an email', () => {
    const built = buildLeadRequest({
      ...EMPTY_LEAD_FORM,
      name: 'Kelechi Obi',
      phone: '0810 550 7713',
      destination: 'Accra',
      budgetMax: '600,000',
    });

    expect(built.ok && built.request).toMatchObject({
      customer: { name: 'Kelechi Obi', email: null, phone: '0810 550 7713' },
      adults: 2,
      children: 0,
      budgetMaxMinor: 60_000_000,
    });
  });

  it('refuses a return before the departure, and a budget upside down', () => {
    const built = buildLeadRequest({
      ...EMPTY_LEAD_FORM,
      name: 'A',
      email: 'a@example.test',
      destination: 'Lagos',
      travelFrom: '2026-10-10',
      travelTo: '2026-10-01',
      budgetMin: '500000',
      budgetMax: '100000',
    });

    expect(!built.ok && Object.keys(built.errors).sort()).toEqual(['budgetMax', 'travelTo']);
  });
});
