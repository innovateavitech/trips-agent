import { describe, expect, it } from 'vitest';
import {
  currentPrice,
  deletability,
  describeEntitlementValue,
  dunningSummary,
  grantsOf,
  parseEntitlementValue,
  publishability,
  subscriptionStatusDisplay,
  tierStatusDisplay,
} from '../billing-rules';
import type { EntitlementCatalogueItem, Subscriber, Tier } from '../types';

function tier(overrides: Partial<Tier> = {}): Tier {
  return {
    id: 'b1ec0000-0000-7000-8000-000000000001',
    code: 'growth',
    name: 'Growth',
    customerDescription: null,
    status: 'Draft',
    trialDays: 0,
    sortOrder: 0,
    isFallback: false,
    subscribers: 0,
    publishedAt: null,
    archivedAt: null,
    prices: [],
    entitlements: [],
    ...overrides,
  };
}

const CATALOGUE: EntitlementCatalogueItem[] = [
  {
    code: 'max_sub_agents',
    name: 'Sub-agents',
    description: 'How many agencies this one may run beneath it.',
    valueType: 'Limit',
    defaultValue: '0',
  },
  {
    code: 'custom_domain',
    name: 'Custom domain',
    description: 'Serve the storefront from the agency’s own hostname.',
    valueType: 'Flag',
    defaultValue: 'false',
  },
  {
    code: 'transaction_fee_bps',
    name: 'Transaction fee',
    description: 'The platform’s share of each sale.',
    valueType: 'Rate',
    defaultValue: '0',
  },
];

describe('deletability', () => {
  it('refuses a plan with subscribers and points at archiving instead', () => {
    const result = deletability(
      tier({ status: 'Published', publishedAt: '2026-01-01T00:00:00Z', subscribers: 3 }),
    );

    expect(result.canDelete).toBe(false);
    expect(result.reason).toContain('3 agencies are');
    expect(result.reason).toContain('Archive it instead');
  });

  it('refuses a plan that has ever been published, even with nobody on it', () => {
    const result = deletability(tier({ status: 'Archived', publishedAt: '2026-01-01T00:00:00Z' }));

    expect(result.canDelete).toBe(false);
    expect(result.reason).toContain('has been published');
  });

  it('allows a draft nobody has ever seen', () => {
    expect(deletability(tier()).canDelete).toBe(true);
  });

  it('gets the singular right for one subscriber', () => {
    const result = deletability(
      tier({ status: 'Published', publishedAt: '2026-01-01T00:00:00Z', subscribers: 1 }),
    );

    expect(result.reason).toContain('1 agency is');
  });
});

describe('publishability', () => {
  it('refuses a plan with no price, because nothing could be charged for it', () => {
    const result = publishability(tier());

    expect(result.canPublish).toBe(false);
    expect(result.reason).toContain('price');
  });

  it('allows the free fallback plan without a price', () => {
    expect(publishability(tier({ isFallback: true })).canPublish).toBe(true);
  });

  it('allows a priced draft', () => {
    const priced = tier({
      prices: [
        {
          id: 'p1',
          currency: 'NGN',
          interval: 'Monthly',
          amountMinor: 2_500_000,
          isPromotional: false,
          effectiveFrom: '2026-01-01T00:00:00Z',
          effectiveTo: null,
        },
      ],
    });

    expect(publishability(priced).canPublish).toBe(true);
    expect(currentPrice(priced)?.amountMinor).toBe(2_500_000);
  });

  it('sends an archived plan through restore first', () => {
    expect(publishability(tier({ status: 'Archived' })).reason).toContain('Restore it first');
  });
});

describe('currentPrice', () => {
  it('ignores a price that has been closed', () => {
    const closed = tier({
      prices: [
        {
          id: 'old',
          currency: 'NGN',
          interval: 'Monthly',
          amountMinor: 1_000_000,
          isPromotional: false,
          effectiveFrom: '2026-01-01T00:00:00Z',
          effectiveTo: '2026-02-01T00:00:00Z',
        },
      ],
    });

    expect(currentPrice(closed)).toBeNull();
  });
});

describe('parseEntitlementValue', () => {
  it('accepts on and off for a flag and refuses anything else', () => {
    expect(parseEntitlementValue('Flag', 'true')).toEqual({ value: 'true' });
    expect(parseEntitlementValue('Flag', 'false')).toEqual({ value: 'false' });
    expect(parseEntitlementValue('Flag', '1')).toHaveProperty('problem');
  });

  it('accepts -1 for an unlimited limit but nothing below it', () => {
    expect(parseEntitlementValue('Limit', '-1')).toEqual({ value: '-1' });
    expect(parseEntitlementValue('Limit', '0')).toEqual({ value: '0' });
    expect(parseEntitlementValue('Limit', '-2')).toHaveProperty('problem');
  });

  it('keeps a fee inside nought to one hundred per cent, in basis points', () => {
    expect(parseEntitlementValue('Rate', '150')).toEqual({ value: '150' });
    expect(parseEntitlementValue('Rate', '10000')).toEqual({ value: '10000' });
    expect(parseEntitlementValue('Rate', '10001')).toHaveProperty('problem');
    expect(parseEntitlementValue('Rate', '-1')).toHaveProperty('problem');
  });

  it('refuses a decimal, because a fee is whole basis points', () => {
    expect(parseEntitlementValue('Rate', '2.5')).toHaveProperty('problem');
  });
});

describe('describeEntitlementValue', () => {
  it('reads the way a person would say it', () => {
    expect(describeEntitlementValue('Flag', 'true')).toBe('on');
    expect(describeEntitlementValue('Flag', 'false')).toBe('off');
    expect(describeEntitlementValue('Limit', '25')).toBe('25');
    expect(describeEntitlementValue('Limit', '-1')).toBe('unlimited');
    expect(describeEntitlementValue('Rate', '150')).toBe('1.5%');
    expect(describeEntitlementValue('Rate', '1000')).toBe('10%');
  });
});

describe('grantsOf', () => {
  it('shows the platform default for anything the plan does not set', () => {
    const rows = grantsOf(
      tier({
        entitlements: [
          {
            code: 'custom_domain',
            name: 'Custom domain',
            valueType: 'Flag',
            value: 'true',
            display: 'on',
          },
        ],
      }),
      CATALOGUE,
    );

    expect(rows).toHaveLength(3);

    const domain = rows.find((row) => row.code === 'custom_domain');
    expect(domain?.granted).toBe(true);
    expect(domain?.value).toBe('true');

    // Not granted, so the default applies — and the default is always the restrictive answer.
    const subAgents = rows.find((row) => row.code === 'max_sub_agents');
    expect(subAgents?.granted).toBe(false);
    expect(subAgents?.value).toBe('0');
  });
});

describe('dunningSummary', () => {
  function subscriber(overrides: Partial<Subscriber> = {}): Subscriber {
    return {
      agencyId: 'a1',
      agencyName: 'Acme Travel Limited',
      agencyStatus: 'Verified',
      subscriptionId: 's1',
      tierName: 'Growth',
      status: 'Active',
      currency: 'NGN',
      amountMinor: 2_500_000,
      currentPeriodStart: '2026-01-01T00:00:00Z',
      currentPeriodEnd: '2026-02-01T00:00:00Z',
      trialEndsAt: null,
      dunningRetries: 0,
      nextDunningAttemptAt: null,
      outstandingMinor: 0,
      ...overrides,
    };
  }

  it('says nothing when nothing is failing', () => {
    expect(dunningSummary(subscriber())).toBeNull();
  });

  it('counts the retries made and the retries left', () => {
    expect(dunningSummary(subscriber({ status: 'PastDue', dunningRetries: 2 }))).toBe(
      '2 of 4 retries made; 2 left.',
    );
  });

  it('warns when the next run will act', () => {
    expect(dunningSummary(subscriber({ status: 'PastDue', dunningRetries: 4 }))).toContain(
      'Every retry has been used',
    );
  });
});

describe('status displays', () => {
  it('always explains what a status means, not only what it is called', () => {
    for (const status of ['Draft', 'Published', 'Archived'] as const) {
      expect(tierStatusDisplay(status).meaning.length).toBeGreaterThan(0);
    }

    for (const status of ['Trialing', 'Active', 'PastDue', 'Cancelled', 'Expired'] as const) {
      expect(subscriptionStatusDisplay(status).meaning.length).toBeGreaterThan(0);
    }
  });

  it('does not treat a past-due agency as a failure', () => {
    expect(subscriptionStatusDisplay('PastDue').tone).toBe('warning');
    expect(subscriptionStatusDisplay('PastDue').meaning).toContain('still applies');
  });
});
