import { describe, expect, it } from 'vitest';
import {
  invoiceAddsUp,
  invoiceStatusDisplay,
  outstanding,
  planActionLabel,
  planChangeConsequences,
  planChangeKind,
  planStatusDisplay,
  urgentNotice,
} from '../billing-rules';
import type { Invoice, MyPlan, Plan } from '../types';

function myPlan(overrides: Partial<MyPlan> = {}): MyPlan {
  return {
    subscriptionId: 's1',
    tierId: 't1',
    planName: 'Growth',
    status: 'Active',
    statusReason: null,
    currency: 'NGN',
    amountMinor: 2_500_000,
    interval: 'Monthly',
    currentPeriodStart: '2026-09-01T00:00:00Z',
    currentPeriodEnd: '2026-10-01T00:00:00Z',
    trialEndsAt: null,
    nextChargeAt: '2026-10-01T00:00:00Z',
    cardOnFile: 'Visa •••• 4242',
    dunningRetries: 0,
    nextDunningAttemptAt: null,
    features: [],
    scheduledChange: null,
    ...overrides,
  };
}

function plan(overrides: Partial<Plan> = {}): Plan {
  return {
    tierId: 't2',
    code: 'starter',
    name: 'Starter',
    description: null,
    currency: 'NGN',
    amountMinor: 500_000,
    interval: 'Monthly',
    trialDays: 0,
    isCurrent: false,
    isFallback: false,
    features: [],
    ...overrides,
  };
}

function invoice(overrides: Partial<Invoice> = {}): Invoice {
  return {
    id: 'i1',
    invoiceNumber: 'TRIPS-INV-2026-000001',
    receiptNumber: null,
    status: 'Open',
    statusReason: null,
    currency: 'NGN',
    totalMinor: 2_500_000,
    issuedAt: '2026-09-01T04:00:00Z',
    dueAt: '2026-09-01T04:00:00Z',
    paidAt: null,
    periodStart: '2026-09-01T00:00:00Z',
    periodEnd: '2026-10-01T00:00:00Z',
    lines: [
      {
        description: 'Growth plan — September',
        quantity: 1,
        unitAmountMinor: 2_500_000,
        amountMinor: 2_500_000,
      },
    ],
    ...overrides,
  };
}

describe('urgentNotice', () => {
  it('says nothing when there is nothing to say', () => {
    expect(urgentNotice(myPlan())).toBeNull();
  });

  it('reassures rather than alarms on the first failed payment', () => {
    const notice = urgentNotice(myPlan({ status: 'PastDue', dunningRetries: 0 }));

    expect(notice?.tone).toBe('warning');
    expect(notice?.detail).toContain('Nothing has changed about your account');
    expect(notice?.detail).toContain('4 more times');
  });

  it('escalates once the retries are nearly gone', () => {
    expect(urgentNotice(myPlan({ status: 'PastDue', dunningRetries: 3 }))?.tone).toBe(
      'destructive',
    );
    expect(urgentNotice(myPlan({ status: 'PastDue', dunningRetries: 4 }))?.detail).toContain(
      'tried every time',
    );
  });

  it('never claims anything has been removed', () => {
    const notice = urgentNotice(myPlan({ status: 'PastDue', dunningRetries: 4 }));

    expect(notice?.detail).toContain('nothing you have built has been removed');
  });

  it('warns a trial with no card behind it, without making it sound like a failure', () => {
    const notice = urgentNotice(
      myPlan({ status: 'Trialing', cardOnFile: null, amountMinor: null }),
    );

    expect(notice?.tone).toBe('info');
    expect(notice?.title).toContain('trial');
  });

  it('warns a paying plan with no card, because the next renewal cannot be charged', () => {
    const notice = urgentNotice(myPlan({ cardOnFile: null }));

    expect(notice?.tone).toBe('warning');
    expect(notice?.detail).toContain('cannot be charged');
  });

  it('prefers the failed payment over the missing card when both are true', () => {
    const notice = urgentNotice(myPlan({ status: 'PastDue', cardOnFile: null, dunningRetries: 1 }));

    expect(notice?.title).toContain('could not take your last payment');
  });
});

describe('planChangeKind', () => {
  it('knows its own plan', () => {
    expect(planChangeKind(plan({ isCurrent: true }), myPlan())).toBe('current');
  });

  it('calls a dearer plan an upgrade and a cheaper one a downgrade', () => {
    expect(planChangeKind(plan({ amountMinor: 5_000_000 }), myPlan())).toBe('upgrade');
    expect(planChangeKind(plan({ amountMinor: 500_000 }), myPlan())).toBe('downgrade');
  });

  it('treats the free plan as a downgrade from a paid one', () => {
    expect(planChangeKind(plan({ amountMinor: null, isFallback: true }), myPlan())).toBe(
      'downgrade',
    );
  });

  it('treats any paid plan as an upgrade from no plan at all', () => {
    expect(planChangeKind(plan(), myPlan({ amountMinor: null, subscriptionId: null }))).toBe(
      'upgrade',
    );
  });
});

describe('planChangeConsequences', () => {
  it('says a downgrade waits for the end of the paid period, and removes nothing', () => {
    const words = planChangeConsequences(plan(), 'downgrade');

    expect(words).toContain('end of the period you have already paid for');
    expect(words).toContain('Nothing you have already set up is removed');
  });

  it('says an upgrade is charged now', () => {
    expect(planChangeConsequences(plan(), 'upgrade')).toContain('pay for the new plan now');
  });

  it('mentions the trial when the plan has one', () => {
    expect(planChangeConsequences(plan({ trialDays: 14 }), 'upgrade')).toContain(
      '14-day free trial',
    );
  });
});

describe('planActionLabel', () => {
  it('never says "switch now" for a change that is not now', () => {
    expect(planActionLabel('downgrade')).toBe('Switch to this');
    expect(planActionLabel('upgrade')).toBe('Upgrade');
    expect(planActionLabel('current')).toBe('Your plan');
  });
});

describe('invoiceAddsUp', () => {
  it('is true when the lines equal the total', () => {
    expect(invoiceAddsUp(invoice())).toBe(true);
  });

  it('multiplies quantity into the line, not the total', () => {
    const multi = invoice({
      totalMinor: 3_000_000,
      lines: [
        { description: 'Seats', quantity: 3, unitAmountMinor: 1_000_000, amountMinor: 3_000_000 },
      ],
    });

    expect(invoiceAddsUp(multi)).toBe(true);
  });

  it('catches a total that does not match its own lines', () => {
    expect(invoiceAddsUp(invoice({ totalMinor: 9_999 }))).toBe(false);
  });
});

describe('outstanding', () => {
  it('lists only what is still owed, oldest first', () => {
    const unpaid = outstanding([
      invoice({ id: 'paid', status: 'Paid', issuedAt: '2026-07-01T00:00:00Z' }),
      invoice({ id: 'newer', status: 'PastDue', issuedAt: '2026-09-01T00:00:00Z' }),
      invoice({ id: 'older', status: 'Open', issuedAt: '2026-08-01T00:00:00Z' }),
      invoice({ id: 'void', status: 'Void', issuedAt: '2026-06-01T00:00:00Z' }),
    ]);

    expect(unpaid.map((row) => row.id)).toEqual(['older', 'newer']);
  });

  it('still counts a written-off invoice, because paying it puts the plan back', () => {
    expect(outstanding([invoice({ status: 'Uncollectible' })])).toHaveLength(1);
  });
});

describe('status displays', () => {
  it('never calls a past-due agency broken', () => {
    const display = planStatusDisplay('PastDue');

    expect(display.tone).toBe('warning');
    expect(display.meaning).toContain('Everything still works');
  });

  it('explains what every status means', () => {
    for (const status of ['Trialing', 'Active', 'PastDue', 'Cancelled', 'Expired', null] as const) {
      expect(planStatusDisplay(status).meaning.length).toBeGreaterThan(0);
    }

    for (const status of ['Open', 'Paid', 'PastDue', 'Uncollectible', 'Void'] as const) {
      expect(invoiceStatusDisplay(status).meaning.length).toBeGreaterThan(0);
    }
  });
});
