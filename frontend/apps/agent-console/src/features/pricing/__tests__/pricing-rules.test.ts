import { describe, expect, it } from 'vitest';
import {
  amountInputFromMinor,
  buildRuleRequest,
  describeRule,
  endedRules,
  formatPercent,
  inheritedRuleLabel,
  MAX_PERCENT_BASIS_POINTS,
  parseAmount,
  parsePercent,
  parseProductId,
  pricingCopy,
  productRulesInForce,
  ruleInForce,
  toWholeNumber,
  type MarkupRule,
  type RuleDraft,
} from '../pricing-rules';

const rule = (over: Partial<MarkupRule> = {}): MarkupRule => ({
  id: '0197a000-0000-7000-8000-000000000001',
  scope: 'Global',
  productType: null,
  productId: null,
  supplierCode: null,
  currency: 'NGN',
  calculationType: 'Percentage',
  percentBasisPoints: 1000,
  valueMinor: null,
  minMarkupMinor: null,
  maxMarkupMinor: null,
  priority: 0,
  appliesToSubAgents: true,
  effectiveFrom: '2026-09-01T00:00:00Z',
  effectiveTo: null,
  supersededById: null,
  status: 'InForce',
  ...over,
});

const draft = (over: Partial<RuleDraft> = {}): RuleDraft => ({
  calculationType: 'Percentage',
  percent: '10',
  amount: '',
  minCap: '',
  maxCap: '',
  ...over,
});

describe('percent to basis points', () => {
  it.each([
    ['10', 1000],
    ['7.5', 750],
    ['12.34', 1234],
    ['0.05', 5],
    ['0', 0],
    ['1000', MAX_PERCENT_BASIS_POINTS],
    [' 10 % ', 1000],
    // The float trap: 1.13 * 100 is 112.99999999999999 in JavaScript.
    ['1.13', 113],
  ])('reads "%s" as %i basis points', (input, expected) => {
    expect(parsePercent(input)).toEqual({ ok: true, value: expected });
  });

  it.each(['', 'ten', '7.555', '-5', '10.', '1000.01', '5%5'])('refuses "%s"', (input) => {
    expect(parsePercent(input).ok).toBe(false);
  });
});

describe('basis points to percent', () => {
  it.each([
    [1000, '10'],
    [750, '7.5'],
    [1234, '12.34'],
    [5, '0.05'],
    [0, '0'],
    [1050, '10.5'],
  ])('writes %i as "%s"', (basisPoints, expected) => {
    expect(formatPercent(basisPoints)).toBe(expected);
  });

  it('round-trips every basis point without drifting', () => {
    for (let basisPoints = 0; basisPoints <= MAX_PERCENT_BASIS_POINTS; basisPoints += 1) {
      expect(parsePercent(formatPercent(basisPoints))).toEqual({ ok: true, value: basisPoints });
    }
  });
});

describe('amounts in and out of kobo', () => {
  it.each([
    ['1500', 150_000],
    ['1,500.50', 150_050],
    ['₦2,000', 200_000],
    ['0.1', 10],
    ['0', 0],
    // 50000.10 * 100 is 5000009.999999999 in floating point.
    ['50000.10', 5_000_010],
  ])('reads "%s" as %i kobo', (input, expected) => {
    expect(parseAmount(input)).toEqual({ ok: true, value: expected });
  });

  it.each(['', '1500abc', '1.234', '-1'])('refuses "%s"', (input) => {
    expect(parseAmount(input).ok).toBe(false);
  });

  it.each([
    [150_000, '1500'],
    [150_050, '1500.50'],
    [5, '0.05'],
  ])('writes %i kobo back as "%s"', (minor, expected) => {
    expect(amountInputFromMinor(minor)).toBe(expected);
  });
});

describe('numbers from the API', () => {
  it('accepts a 64-bit integer sent as a string', () => {
    expect(toWholeNumber('8500000')).toBe(8_500_000);
  });

  it('refuses one that would lose precision rather than rounding it', () => {
    expect(() => toWholeNumber('9007199254740993')).toThrow();
  });
});

describe('product ids', () => {
  it('accepts a uuid and lower-cases it', () => {
    expect(parseProductId(' 0197A000-0000-7000-8000-0000000000F1 ')).toEqual({
      ok: true,
      value: '0197a000-0000-7000-8000-0000000000f1',
    });
  });

  it('refuses anything else', () => {
    expect(parseProductId('Lagos city tour').ok).toBe(false);
  });
});

describe('which rule sits in a slot', () => {
  it('ignores rules that have ended or not started', () => {
    const ended = rule({ id: 'a', effectiveTo: '2026-09-10T00:00:00Z', status: 'Ended' });
    const future = rule({ id: 'b', effectiveFrom: '2026-10-01T00:00:00Z', status: 'Scheduled' });
    const current = rule({ id: 'c' });

    expect(ruleInForce([ended, future, current], { scope: 'Global' })?.id).toBe('c');
  });

  // The reviewer's case. The server replaced 10% with 15% at T and the list
  // came back while this browser's clock read T − 1.8s. By the timestamps the
  // old rule is still in force and the new one has not started; by the
  // server, which stamped them, it is the other way round.
  it("trusts the server's status over this browser's clock", () => {
    const serverNow = new Date(Date.now() + 60_000).toISOString();
    const replaced = rule({
      id: 'old',
      percentBasisPoints: 1000,
      effectiveTo: serverNow,
      supersededById: 'new',
      status: 'Ended',
    });
    const replacement = rule({
      id: 'new',
      percentBasisPoints: 1500,
      effectiveFrom: serverNow,
      status: 'InForce',
    });

    expect(ruleInForce([replaced, replacement], { scope: 'Global' })?.id).toBe('new');
    expect(endedRules([replaced, replacement]).map((r) => r.id)).toEqual(['old']);
  });

  it('prefers the higher priority, then the later start', () => {
    const older = rule({ id: 'a', priority: 5, effectiveFrom: '2026-01-01T00:00:00Z' });
    const newer = rule({ id: 'b', priority: 5, effectiveFrom: '2026-09-01T00:00:00Z' });
    const lower = rule({ id: 'c', priority: 0, effectiveFrom: '2026-09-10T00:00:00Z' });

    expect(ruleInForce([older, lower, newer], { scope: 'Global' })?.id).toBe('b');
  });

  it('keeps product types apart', () => {
    const tours = rule({ id: 't', scope: 'ProductType', productType: 'Tour' });

    expect(ruleInForce([tours], { scope: 'ProductType', productType: 'Flight' })).toBeUndefined();
    expect(ruleInForce([tours], { scope: 'ProductType', productType: 'Tour' })?.id).toBe('t');
  });

  it('lists one product rule per product', () => {
    const productId = '0197a000-0000-7000-8000-0000000000f1';
    const first = rule({ id: 'a', scope: 'Product', productType: 'Tour', productId });
    const second = rule({ id: 'b', scope: 'Product', productType: 'Tour', productId, priority: 1 });

    expect(productRulesInForce([first, second]).map((r) => r.id)).toEqual(['b']);
  });

  it('keeps ended rules for the history, most recently ended first', () => {
    const early = rule({ id: 'a', effectiveTo: '2026-09-02T00:00:00Z', status: 'Ended' });
    const late = rule({ id: 'b', effectiveTo: '2026-09-09T00:00:00Z', status: 'Ended' });

    expect(endedRules([early, rule({ id: 'c' }), late]).map((r) => r.id)).toEqual(['b', 'a']);
  });

  it('does not call a rule with a future end ended', () => {
    const endsLater = rule({ id: 'a', effectiveTo: '2027-01-01T00:00:00Z', status: 'InForce' });

    expect(endedRules([endsLater])).toEqual([]);
    expect(ruleInForce([endsLater], { scope: 'Global' })?.id).toBe('a');
  });
});

describe('what the screen says about precedence', () => {
  it('tells a principal the most specific rule wins', () => {
    const copy = pricingCopy({ hasPrincipal: false, hasOwnDefault: false });

    expect(copy.precedence).toContain('most specific rule always wins');
    expect(copy.noDefault).toContain('Travellers pay the net price');
  });

  // A sub-agent's own default beats even its principal's rule for flights, so
  // "the most specific rule wins" and "travellers pay the net price" are both
  // false for it, and led a sub-agent to switch its principal's rules off.
  it("tells a sub-agent its own rules outrank all of its principal's", () => {
    const copy = pricingCopy({ hasPrincipal: true, hasOwnDefault: false });

    expect(copy.precedence).not.toContain('always wins');
    expect(copy.precedence).toContain("principal agency's rules apply only");
    expect(copy.defaultDescription).toContain("none of your principal agency's rules apply");
    expect(copy.noDefault).toContain("principal agency's rules apply");
    expect(copy.noTypeRule).toContain('principal');
  });

  it("sends a sub-agent's empty product-type row to its own default once it has one", () => {
    expect(pricingCopy({ hasPrincipal: true, hasOwnDefault: true }).noTypeRule).toBe(
      'Uses your default.',
    );
  });

  it('names inherited rules so they can be told apart', () => {
    expect(
      inheritedRuleLabel({
        scope: 'ProductType',
        productType: 'Flight',
        productId: null,
        supplierCode: null,
      }),
    ).toBe('Flights rule');
    expect(
      inheritedRuleLabel({
        scope: 'Supplier',
        productType: null,
        productId: null,
        supplierCode: 'trips_africa',
      }),
    ).toBe('Supplier rule · trips_africa');
  });
});

describe('the request a form sends', () => {
  it('sends a percentage as basis points, with optional caps in kobo', () => {
    const built = buildRuleRequest(
      draft({ percent: '7.5', minCap: '2,000', maxCap: '50000' }),
      { scope: 'ProductType', productType: 'Flight' },
      'NGN',
    );

    expect(built).toMatchObject({
      ok: true,
      request: {
        scope: 'ProductType',
        productType: 'Flight',
        productId: null,
        calculationType: 'Percentage',
        percentBasisPoints: 750,
        valueMinor: null,
        minMarkupMinor: 200_000,
        maxMarkupMinor: 5_000_000,
        appliesToSubAgents: true,
      },
    });
  });

  it('refuses a minimum above the maximum', () => {
    const built = buildRuleRequest(
      draft({ minCap: '500', maxCap: '100' }),
      { scope: 'Global' },
      'NGN',
    );

    expect(built.ok).toBe(false);
    if (!built.ok) expect(built.errors.maxCap).toBeDefined();
  });

  it('sends a fixed amount and no caps, even if caps were typed before switching', () => {
    const built = buildRuleRequest(
      draft({ calculationType: 'Fixed', amount: '1500', minCap: '10', maxCap: '20' }),
      { scope: 'Global' },
      'NGN',
    );

    expect(built).toMatchObject({
      ok: true,
      request: {
        valueMinor: 150_000,
        percentBasisPoints: null,
        minMarkupMinor: null,
        maxMarkupMinor: null,
      },
    });
  });

  it('keeps the replaced rule’s priority and sub-agent setting', () => {
    const replacing = rule({ priority: 7, appliesToSubAgents: false });

    const built = buildRuleRequest(draft(), { scope: 'Global' }, 'NGN', replacing);

    expect(built).toMatchObject({ ok: true, request: { priority: 7, appliesToSubAgents: false } });
  });
});

describe('describing a rule', () => {
  it('says what a percentage rule does, caps included', () => {
    const text = describeRule(rule({ minMarkupMinor: 200_000, maxMarkupMinor: '5000000' }), 'NGN');

    expect(text).toContain('10% of the net rate');
    expect(text).toContain('at least');
    expect(text).toContain('2,000.00');
    expect(text).toContain('50,000.00');
  });

  it('says what a fixed rule does', () => {
    const text = describeRule(
      rule({ calculationType: 'Fixed', percentBasisPoints: null, valueMinor: 150_000 }),
      'NGN',
    );

    expect(text).toMatch(/^A fixed .*1,500\.00$/);
  });
});
