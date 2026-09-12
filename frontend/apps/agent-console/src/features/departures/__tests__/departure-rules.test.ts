import { describe, expect, it } from 'vitest';
import {
  buildDepartureRequest,
  describeGuarantee,
  describeSeats,
  draftFromDeparture,
  emptyDepartureDraft,
  groupCounts,
  newRowKey,
  paymentSchedule,
  plusDays,
  priceForParty,
  tierMins,
  validateDeparture,
  type DepartureDraft,
} from '../departure-rules';
import type { DepartureRequest } from '../types';

const TODAY = '2026-09-11';

function request(patch: Partial<DepartureRequest> = {}): DepartureRequest {
  return {
    departureDate: '2026-12-01',
    isGroupDeparture: true,
    minPax: 6,
    capacityTotal: 16,
    cutoffDaysBefore: 21,
    depositType: 'Percent',
    depositPercentBasisPoints: 3000,
    depositAmountMinor: null,
    priceTiers: [
      { minPax: 1, maxPax: 3, pricePerPaxMinor: 145_000_000 },
      { minPax: 4, maxPax: null, pricePerPaxMinor: 139_000_000 },
    ],
    installments: [],
    ...patch,
  };
}

describe('checking a departure', () => {
  it('accepts sound terms', () => {
    expect(validateDeparture(request(), TODAY)).toEqual({});
  });

  it('names every problem at once', () => {
    const problems = validateDeparture(
      request({
        departureDate: '2026-09-01',
        minPax: 20,
        depositPercentBasisPoints: 0,
        priceTiers: [],
      }),
      TODAY,
    );

    expect(Object.keys(problems).sort()).toEqual([
      'departureDate',
      'deposit',
      'minPax',
      'priceTiers',
    ]);
  });

  it('refuses party sizes with a gap or an overlap', () => {
    const gap = request({
      priceTiers: [
        { minPax: 1, maxPax: 3, pricePerPaxMinor: 100 },
        { minPax: 5, maxPax: null, pricePerPaxMinor: 90 },
      ],
    });

    expect(validateDeparture(gap, TODAY)['priceTiers']).toMatch(/no gaps or overlaps/);
  });

  it('allows only the last party size to be open-ended', () => {
    const open = request({
      priceTiers: [
        { minPax: 1, maxPax: null, pricePerPaxMinor: 100 },
        { minPax: 2, maxPax: null, pricePerPaxMinor: 90 },
      ],
    });

    expect(validateDeparture(open, TODAY)['priceTiers.0.maxPax']).toBeDefined();
  });

  it('refuses a fixed deposit bigger than a seat', () => {
    const problems = validateDeparture(
      request({
        depositType: 'Fixed',
        depositPercentBasisPoints: null,
        depositAmountMinor: 200_000_000,
      }),
      TODAY,
    );

    expect(problems['deposit']).toMatch(/More than the price of a seat/);
  });

  it('wants the balance payments to cover all of the balance', () => {
    const problems = validateDeparture(
      request({
        installments: [
          {
            sequence: 1,
            dueBasis: 'BeforeDeparture',
            dueOffsetDays: 60,
            percentOfBalanceBasisPoints: 5000,
          },
          {
            sequence: 2,
            dueBasis: 'BeforeDeparture',
            dueOffsetDays: 30,
            percentOfBalanceBasisPoints: 4000,
          },
        ],
      }),
      TODAY,
    );

    expect(problems['installments']).toBe('The payments add up to 90% of the balance, not 100%.');
  });
});

describe('the form', () => {
  const draft: DepartureDraft = {
    ...emptyDepartureDraft(145_000_000),
    departureDate: '2026-12-01',
    tiers: [
      { key: newRowKey(), maxPax: '3', price: '1,450,000' },
      { key: newRowKey(), maxPax: '7', price: '1390000' },
      { key: newRowKey(), maxPax: '', price: '1320000' },
    ],
  };

  it('starts each party size where the one before it ends', () => {
    expect(tierMins(draft.tiers)).toEqual([1, 4, 8]);
  });

  it('builds a request the rules accept', () => {
    const built = buildDepartureRequest(draft);

    expect(built.ok).toBe(true);
    if (!built.ok) return;
    expect(built.request.priceTiers).toEqual([
      { minPax: 1, maxPax: 3, pricePerPaxMinor: 145_000_000 },
      { minPax: 4, maxPax: 7, pricePerPaxMinor: 139_000_000 },
      { minPax: 8, maxPax: null, pricePerPaxMinor: 132_000_000 },
    ]);
    expect(built.request.depositPercentBasisPoints).toBe(3000);
    expect(validateDeparture(built.request, TODAY)).toEqual({});
  });

  it('says which field it could not read', () => {
    const built = buildDepartureRequest({
      ...draft,
      capacityTotal: 'twenty',
      depositPercent: '150',
    });

    expect(built.ok).toBe(false);
    if (built.ok) return;
    expect(built.errors).toEqual({ capacityTotal: 'A whole number.', deposit: 'At most 100%.' });
  });

  it('reads a departure back into the form unchanged', () => {
    const original = request({
      installments: [
        {
          sequence: 1,
          dueBasis: 'FromBooking',
          dueOffsetDays: 30,
          percentOfBalanceBasisPoints: 2500,
        },
        {
          sequence: 2,
          dueBasis: 'BeforeDeparture',
          dueOffsetDays: 30,
          percentOfBalanceBasisPoints: 7500,
        },
      ],
    });
    const built = buildDepartureRequest(draftFromDeparture(original));

    expect(built.ok && built.request).toEqual(original);
  });
});

describe('the payment schedule', () => {
  it('takes the deposit at booking and the balance when bookings close', () => {
    expect(paymentSchedule(request(), TODAY, 145_000_000)).toEqual([
      { label: 'Deposit', dueDate: TODAY, amountMinor: 43_500_000, dueNow: true },
      { label: 'Balance', dueDate: '2026-11-10', amountMinor: 101_500_000, dueNow: false },
    ]);
  });

  it('spreads the balance, and the last payment takes the rounding so it adds up exactly', () => {
    const lines = paymentSchedule(
      request({
        installments: [
          {
            sequence: 1,
            dueBasis: 'BeforeDeparture',
            dueOffsetDays: 60,
            percentOfBalanceBasisPoints: 3333,
          },
          {
            sequence: 2,
            dueBasis: 'BeforeDeparture',
            dueOffsetDays: 30,
            percentOfBalanceBasisPoints: 6667,
          },
        ],
      }),
      TODAY,
      100_001,
    );

    expect(lines.reduce((sum, line) => sum + line.amountMinor, 0)).toBe(100_001);
    expect(lines.map((line) => line.dueDate)).toEqual([TODAY, '2026-10-02', '2026-11-01']);
  });

  it('makes a payment whose date has passed due when they book', () => {
    const late = paymentSchedule(
      request({
        departureDate: '2026-10-01',
        installments: [
          {
            sequence: 1,
            dueBasis: 'BeforeDeparture',
            dueOffsetDays: 60,
            percentOfBalanceBasisPoints: 10_000,
          },
        ],
      }),
      TODAY,
      100_000,
    );

    expect(late[1]).toMatchObject({ dueDate: TODAY, dueNow: true });
  });
});

describe('small things', () => {
  it('prices a party by its size', () => {
    const tiers = request().priceTiers;

    expect(priceForParty(tiers, 2)).toBe(145_000_000);
    expect(priceForParty(tiers, 9)).toBe(139_000_000);
  });

  it('counts the days across a month end', () => {
    expect(plusDays('2026-01-31', 1)).toBe('2026-02-01');
    expect(plusDays('2026-03-01', -1)).toBe('2026-02-28');
  });

  it('says whether a group departure runs yet', () => {
    expect(describeGuarantee({ isGroupDeparture: true, minPax: 6, capacityConfirmed: 2 })).toBe(
      'Needs 4 more paid travellers to run.',
    );
    expect(describeGuarantee({ isGroupDeparture: true, minPax: 6, capacityConfirmed: 9 })).toMatch(
      /^It runs/,
    );
    expect(
      describeGuarantee({ isGroupDeparture: false, minPax: 1, capacityConfirmed: 0 }),
    ).toBeNull();
  });

  it('describes the seats, and who is only holding one', () => {
    expect(describeSeats({ capacityTotal: 16, capacityConfirmed: 9, capacityReserved: 2 })).toBe(
      '11 of 16 taken · 2 held in checkout',
    );
  });

  it('counts departures by what an agent does with them', () => {
    expect(
      groupCounts([
        { status: 'Open' },
        { status: 'NearlyFull' },
        { status: 'SoldOut' },
        { status: 'Cancelled' },
      ]),
    ).toEqual({ all: 4, selling: 2, soldOut: 1, closed: 1 });
  });
});
