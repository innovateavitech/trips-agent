import { describe, expect, it } from 'vitest';
import {
  bookingsForProduct,
  bucketOf,
  buildQuoteRequest,
  customerInsights,
  customerTripStage,
  describeBudget,
  describeDates,
  describeTrip,
  draftFromQuote,
  filterCustomers,
  groupByStage,
  newCrmKey,
  quoteTotalMinor,
  relativeTime,
  searchLeads,
  whyNotSendable,
  type QuoteDraft,
} from '../crm-rules';
import { buildLeadRequest, EMPTY_LEAD_FORM } from '../lead-form';
import type { CustomerBooking, CustomerSummary, LeadSummary } from '../types';

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

describe('the customers list', () => {
  function customer(patch: Partial<CustomerSummary>): CustomerSummary {
    return {
      id: 'c1',
      name: 'Adeola Martins',
      email: 'adeola@example.test',
      phone: '0809 115 3348',
      lifetimeValueMinor: 0,
      totalBookings: 0,
      lastActivityAt: '2026-09-01T09:00:00Z',
      openLeadCount: 0,
      ...patch,
    };
  }

  const NO_FILTERS = {
    query: '',
    lastBooking: { from: '', to: '' },
    dateAdded: { from: '', to: '' },
  };

  const adeola = customer({});
  const harbour = customer({
    id: 'c2',
    name: 'Harbour Point Logistics',
    email: 'travel@harbourpoint.example.test',
    phone: null,
    kind: 'business',
    lastBookingAt: '2026-04-18T10:00:00Z',
    createdAt: '2026-03-02T09:00:00Z',
    lastActivityAt: '2026-09-10T09:00:00Z',
  });

  it('puts the most recently active customer first', () => {
    expect(filterCustomers([adeola, harbour], NO_FILTERS).map((c) => c.id)).toEqual(['c2', 'c1']);
  });

  it('matches name, email or phone, ignoring case', () => {
    const ids = (query: string) =>
      filterCustomers([adeola, harbour], { ...NO_FILTERS, query }).map((c) => c.id);
    expect(ids('HARBOUR')).toEqual(['c2']);
    expect(ids('adeola@')).toEqual(['c1']);
    expect(ids('0809')).toEqual(['c1']);
    expect(ids('nobody')).toEqual([]);
  });

  it('hides customers with no last booking once a last-booking range is set', () => {
    const inApril = { ...NO_FILTERS, lastBooking: { from: '2026-04-01', to: '2026-04-30' } };
    expect(filterCustomers([adeola, harbour], inApril).map((c) => c.id)).toEqual(['c2']);
  });

  it('filters by the day the customer was added, in Lagos', () => {
    const march = { ...NO_FILTERS, dateAdded: { from: '2026-03-01', to: '2026-03-31' } };
    const may = { ...NO_FILTERS, dateAdded: { from: '2026-05-01', to: '' } };
    expect(filterCustomers([adeola, harbour], march).map((c) => c.id)).toEqual(['c2']);
    expect(filterCustomers([adeola, harbour], may)).toEqual([]);
  });
});

describe('one customer', () => {
  function booking(patch: Partial<CustomerBooking>): CustomerBooking {
    return {
      reference: 'TRP-1',
      title: 'Lagos → Abuja, Air Peace',
      travelDate: '2026-09-20',
      status: 'Ticketed',
      amountMinor: 10_000_000,
      ...patch,
    };
  }

  describe('where a trip stands', () => {
    it('reads the travel day against today in Lagos', () => {
      const stage = (travelDate: string) => customerTripStage(booking({ travelDate }), NOW);
      expect(stage('2026-09-12')).toBe('upcoming');
      expect(stage('2026-09-11')).toBe('active');
      expect(stage('2026-09-10')).toBe('completed');
    });

    it('uses the Lagos day, not UTC, just after midnight in Lagos', () => {
      // 00:30 in Lagos on the 12th is still 23:30 on the 11th in UTC.
      const justAfterMidnight = new Date('2026-09-12T00:30:00+01:00');
      expect(customerTripStage(booking({ travelDate: '2026-09-12' }), justAfterMidnight)).toBe(
        'active',
      );
    });

    it('has no stage without a travel date, or once cancelled or refunded', () => {
      expect(customerTripStage(booking({ travelDate: null }), NOW)).toBeNull();
      expect(customerTripStage(booking({ status: 'Refunded' }), NOW)).toBeNull();
      expect(customerTripStage(booking({ status: 'Cancelled' }), NOW)).toBeNull();
    });
  });

  it('filters by product and puts the newest booking first', () => {
    const trips = [
      booking({ reference: 'old-flight', product: 'flight', bookedAt: '2026-08-01T10:00:00Z' }),
      booking({ reference: 'bus', product: 'bus', bookedAt: '2026-09-01T10:00:00Z' }),
      booking({ reference: 'tour', bookedAt: '2026-09-05T10:00:00Z' }),
    ];
    expect(bookingsForProduct(trips, 'all').map((b) => b.reference)).toEqual([
      'tour',
      'bus',
      'old-flight',
    ]);
    expect(bookingsForProduct(trips, 'flight').map((b) => b.reference)).toEqual(['old-flight']);
    expect(bookingsForProduct(trips, 'bus').map((b) => b.reference)).toEqual(['bus']);
  });

  describe('the figures', () => {
    const LAGOS_ABUJA = { from: 'Lagos', to: 'Abuja', fromCode: 'LOS', toCode: 'ABV' };
    const ENUGU_LAGOS = { from: 'Enugu', to: 'Lagos', fromCode: 'ENU', toCode: 'LOS' };

    it('is all empty with no bookings', () => {
      expect(customerInsights([])).toEqual({
        tripCount: 0,
        totalMinor: 0,
        averageMinor: null,
        biggestMinor: null,
        topRoute: null,
        topAirline: null,
        flightMinor: 0,
        busMinor: 0,
        lastBookedAt: null,
      });
    });

    it('averages to whole kobo and finds the biggest trip', () => {
      const insights = customerInsights([
        booking({ amountMinor: 100 }),
        booking({ amountMinor: 100 }),
        booking({ amountMinor: 101 }),
      ]);
      expect(insights.averageMinor).toBe(100);
      expect(Number.isInteger(insights.averageMinor)).toBe(true);
      expect(insights.biggestMinor).toBe(101);
    });

    it('picks the most-booked route and airline, and never a bus company as an airline', () => {
      const insights = customerInsights([
        booking({ product: 'bus', route: ENUGU_LAGOS, carrier: 'Libra Motors' }),
        booking({ product: 'bus', route: ENUGU_LAGOS, carrier: 'Libra Motors' }),
        booking({ product: 'bus', route: ENUGU_LAGOS, carrier: 'Libra Motors' }),
        booking({ product: 'flight', route: LAGOS_ABUJA, carrier: 'Air Peace' }),
      ]);
      expect(insights.topRoute).toBe('ENU – LOS');
      expect(insights.topAirline).toBe('Air Peace');
    });

    it('breaks a tie on count by the larger spend', () => {
      const insights = customerInsights([
        booking({ product: 'flight', route: LAGOS_ABUJA, carrier: 'Air Peace', amountMinor: 100 }),
        booking({ product: 'flight', route: ENUGU_LAGOS, carrier: 'Ibom Air', amountMinor: 900 }),
      ]);
      expect(insights.topRoute).toBe('ENU – LOS');
      expect(insights.topAirline).toBe('Ibom Air');
    });

    it('splits spend by product and leaves tours out of both', () => {
      const insights = customerInsights([
        booking({ product: 'flight', amountMinor: 700 }),
        booking({ product: 'bus', amountMinor: 200 }),
        booking({ amountMinor: 100 }),
      ]);
      expect([insights.flightMinor, insights.busMinor, insights.totalMinor]).toEqual([
        700, 200, 1000,
      ]);
    });

    it('knows the most recent booking date', () => {
      const insights = customerInsights([
        booking({ bookedAt: '2026-09-01T10:00:00Z' }),
        booking({ bookedAt: '2026-09-09T10:00:00Z' }),
        booking({}),
      ]);
      expect(insights.lastBookedAt).toBe('2026-09-09T10:00:00Z');
    });
  });
});
