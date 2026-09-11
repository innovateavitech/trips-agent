import type { Schemas } from '@trips/api-client';
import { formatMoney } from '@trips/utils';

/**
 * The arithmetic and bookkeeping behind the pricing screen, as plain functions.
 *
 * Kept out of the components so it can be tested without rendering, and so the
 * one rule that matters most — money and percentages are whole numbers, never
 * floats — lives in a single place that a test can hold to it.
 *
 * NOTHING here decides a price. The server does that (`POST /pricing/preview`),
 * and the screen shows what it says. These functions only turn what the agent
 * typed into the whole numbers the API takes, and back.
 */

export type MarkupRule = Schemas['MarkupRuleResponse'];
export type MarkupRuleRequest = Schemas['MarkupRuleRequest'];
/** One of the principal's rules a sub-agent inherits. Read-only here. */
export type InheritedRule = Schemas['InheritedMarkupRuleResponse'];

export type ProductType = 'Flight' | 'Bus' | 'Tour' | 'Package' | 'Visa' | 'GroupDeparture';
export type CalculationType = 'Percentage' | 'Fixed';

export const PRODUCT_TYPES: ReadonlyArray<{ value: ProductType; label: string }> = [
  { value: 'Flight', label: 'Flights' },
  { value: 'Bus', label: 'Buses' },
  { value: 'Tour', label: 'Tours' },
  { value: 'Package', label: 'Packages' },
  { value: 'Visa', label: 'Visas' },
  { value: 'GroupDeparture', label: 'Group tours' },
];

/** 1000%, the server's ceiling. It exists to catch a units slip, not to limit anyone. */
export const MAX_PERCENT_BASIS_POINTS = 100_000;

export type Parsed<T> = { ok: true; value: T } | { ok: false; error: string };

/**
 * The API sends 64-bit integers as `number | string`, because a JSON number
 * cannot hold every 64-bit value exactly. Every figure here is far below that
 * limit, so it becomes a plain number — but only if it is still exact.
 */
export function toWholeNumber(value: number | string): number {
  const number = typeof value === 'number' ? value : Number(value);
  if (!Number.isSafeInteger(number)) {
    throw new Error(`Expected a whole number from the API, got "${value}".`);
  }
  return number;
}

export function toOptionalWholeNumber(value: number | string | null | undefined): number | null {
  return value === null || value === undefined ? null : toWholeNumber(value);
}

/**
 * "7.5" → 750 basis points. Two decimal places at most, because a basis point
 * is a hundredth of a percent and there is nothing finer to store.
 *
 * Integer arithmetic on the digits, never `parseFloat(x) * 100`: in JavaScript
 * `1.13 * 100` is 112.99999999999999, and truncating that loses a basis point.
 */
export function parsePercent(input: string): Parsed<number> {
  const cleaned = input.trim().replace(/%$/, '').trim();

  if (cleaned === '') return { ok: false, error: 'Enter a percentage.' };

  if (!/^\d+(\.\d{1,2})?$/.test(cleaned)) {
    return { ok: false, error: 'Enter a plain percentage, like 10 or 7.5.' };
  }

  const [whole = '0', fraction = ''] = cleaned.split('.');
  const basisPoints = Number(whole) * 100 + Number(fraction.padEnd(2, '0'));

  if (!Number.isSafeInteger(basisPoints) || basisPoints > MAX_PERCENT_BASIS_POINTS) {
    return { ok: false, error: 'A markup can be at most 1000%.' };
  }

  return { ok: true, value: basisPoints };
}

/** 750 → "7.5", 1000 → "10", 1234 → "12.34". The inverse of {@link parsePercent}. */
export function formatPercent(basisPoints: number): string {
  const whole = Math.trunc(basisPoints / 100);
  const hundredths = basisPoints % 100;

  if (hundredths === 0) return String(whole);

  return `${whole}.${String(hundredths).padStart(2, '0').replace(/0$/, '')}`;
}

/**
 * "1,500.50" → 150050 kobo. Same rules as the wallet's top-up amount, without
 * its limits: a markup of ₦0 is allowed, and there is no minimum.
 */
export function parseAmount(input: string): Parsed<number> {
  const cleaned = input.trim().replace(/[\s,₦]/g, '');

  if (cleaned === '') return { ok: false, error: 'Enter an amount.' };

  if (!/^\d+(\.\d{1,2})?$/.test(cleaned)) {
    return { ok: false, error: 'Enter a plain amount, like 2000 or 2000.50.' };
  }

  const [whole = '0', fraction = ''] = cleaned.split('.');
  const amountMinor = Number(whole) * 100 + Number(fraction.padEnd(2, '0'));

  if (!Number.isSafeInteger(amountMinor)) return { ok: false, error: 'That amount is too large.' };

  return { ok: true, value: amountMinor };
}

/** 150000 → "1500", 150050 → "1500.50" — what goes back into a text box when editing. */
export function amountInputFromMinor(amountMinor: number): string {
  const whole = Math.trunc(amountMinor / 100);
  const minor = amountMinor % 100;
  return minor === 0 ? String(whole) : `${whole}.${String(minor).padStart(2, '0')}`;
}

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export function parseProductId(input: string): Parsed<string> {
  const cleaned = input.trim();
  if (cleaned === '') return { ok: false, error: 'Enter the product ID.' };
  if (!UUID.test(cleaned)) {
    return { ok: false, error: 'That is not a product ID. Copy it from the product page.' };
  }
  return { ok: true, value: cleaned.toLowerCase() };
}

// ----------------------------------------------------------------- which rule is where

/** The places a rule can sit on this screen. Supplier rules exist but are not set up here. */
export type RuleSlot =
  | { scope: 'Global' }
  | { scope: 'ProductType'; productType: ProductType }
  | { scope: 'Product'; productType: ProductType; productId: string };

/**
 * In force, by the server's word. Never worked out here from the timestamps:
 * the server stamps a replacement's start and a retired rule's end with its own
 * clock, and a browser clock a second or two behind would read the rule just
 * replaced as still in force and its replacement as not started — until reload.
 */
export function isInForce(rule: Pick<MarkupRule, 'status'>): boolean {
  return rule.status === 'InForce';
}

/** Retired, replaced or past its end — by the server's word, like {@link isInForce}. */
export function hasEnded(rule: Pick<MarkupRule, 'status'>): boolean {
  return rule.status === 'Ended';
}

function occupies(rule: MarkupRule, slot: RuleSlot): boolean {
  if (rule.scope !== slot.scope) return false;
  if (slot.scope === 'Global') return true;
  if (rule.productType !== slot.productType) return false;
  return slot.scope === 'ProductType' || rule.productId?.toLowerCase() === slot.productId;
}

/**
 * Most-important-first, the way the server breaks a tie inside one slot:
 * higher priority, then the later start. (The server has one more tie-break,
 * by id; the preview is what to trust when two rules share a slot.)
 */
function byImportance(a: MarkupRule, b: MarkupRule): number {
  const priority = toWholeNumber(b.priority) - toWholeNumber(a.priority);
  if (priority !== 0) return priority;
  return new Date(b.effectiveFrom).getTime() - new Date(a.effectiveFrom).getTime();
}

/** The rule in force in `slot`, or undefined when the slot is empty. */
export function ruleInForce(rules: readonly MarkupRule[], slot: RuleSlot): MarkupRule | undefined {
  return rules.filter((rule) => occupies(rule, slot) && isInForce(rule)).sort(byImportance)[0];
}

/** One rule per product that has one in force, newest product rule first. */
export function productRulesInForce(rules: readonly MarkupRule[]): MarkupRule[] {
  const winners = new Map<string, MarkupRule>();

  for (const rule of [...rules].filter((r) => r.scope === 'Product' && isInForce(r))) {
    const key = `${rule.productType}:${rule.productId?.toLowerCase()}`;
    const current = winners.get(key);
    if (current === undefined || byImportance(rule, current) < 0) winners.set(key, rule);
  }

  return [...winners.values()].sort(
    (a, b) => new Date(b.effectiveFrom).getTime() - new Date(a.effectiveFrom).getTime(),
  );
}

/** Rules that have stopped applying — kept, so every past price can still be explained. */
export function endedRules(rules: readonly MarkupRule[]): MarkupRule[] {
  return rules
    .filter((rule) => hasEnded(rule))
    .sort(
      (a, b) => new Date(b.effectiveTo ?? 0).getTime() - new Date(a.effectiveTo ?? 0).getTime(),
    );
}

// ----------------------------------------------------------------- words

export function productTypeLabel(productType: string | null): string {
  return PRODUCT_TYPES.find((type) => type.value === productType)?.label ?? 'Products';
}

/** "Default markup", "Flights rule", "Single-product rule", "Supplier rule". */
export function scopeLabel(rule: Pick<MarkupRule, 'scope' | 'productType'>): string {
  switch (rule.scope) {
    case 'Global':
      return 'Default markup';
    case 'ProductType':
      return `${productTypeLabel(rule.productType)} rule`;
    case 'Product':
      return 'Single-product rule';
    case 'Supplier':
      return 'Supplier rule';
    default:
      return 'Rule';
  }
}

/** "Flights rule", "Tours · <id>", "Supplier rule · trips_africa" — a read-only row's name. */
export function inheritedRuleLabel(
  rule: Pick<InheritedRule, 'scope' | 'productType' | 'productId' | 'supplierCode'>,
): string {
  if (rule.scope === 'Product')
    return `${productTypeLabel(rule.productType)} · ${rule.productId ?? ''}`;
  if (rule.scope === 'Supplier') return `Supplier rule · ${rule.supplierCode ?? ''}`;
  return scopeLabel(rule);
}

type RuleTerms = Pick<
  MarkupRule,
  'calculationType' | 'percentBasisPoints' | 'valueMinor' | 'minMarkupMinor' | 'maxMarkupMinor'
>;

/** "10% of the net rate, at least ₦2,000.00" or "A fixed ₦1,500.00". */
export function describeRule(rule: RuleTerms, currency: string): string {
  if (rule.calculationType === 'Fixed') {
    return `A fixed ${formatMoney(toOptionalWholeNumber(rule.valueMinor) ?? 0, currency)}`;
  }

  let text = `${formatPercent(toOptionalWholeNumber(rule.percentBasisPoints) ?? 0)}% of the net rate`;
  const floor = toOptionalWholeNumber(rule.minMarkupMinor);
  const ceiling = toOptionalWholeNumber(rule.maxMarkupMinor);
  if (floor !== null) text += `, at least ${formatMoney(floor, currency)}`;
  if (ceiling !== null) text += `, at most ${formatMoney(ceiling, currency)}`;
  return text;
}

// ----------------------------------------------------------------- who wins, in words

/**
 * The sentences on this screen that depend on whether the agency is a
 * sub-agent. They differ because the precedence does: a sub-agent's own rules
 * are all consulted before any of its principal's, so its own *default* beats
 * even the principal's rule for one product. Telling a sub-agent "the most
 * specific rule wins" invites exactly the mistake that rule causes.
 */
export interface PricingCopy {
  /** Under "Which rule wins?". */
  precedence: string;
  /** Under "Default markup". */
  defaultDescription: string;
  /** The default row when the agency has none. */
  noDefault: string;
  /** A product-type row with no rule of its own. */
  noTypeRule: string;
}

export function pricingCopy({
  hasPrincipal,
  hasOwnDefault,
}: {
  hasPrincipal: boolean;
  hasOwnDefault: boolean;
}): PricingCopy {
  if (!hasPrincipal) {
    return {
      precedence:
        'The most specific rule always wins: a single product, then a product type, then your default.',
      defaultDescription:
        'Applies to everything you sell, unless a more specific rule below says otherwise.',
      noDefault: 'No default. Travellers pay the net price unless another rule applies.',
      noTypeRule: 'Uses your default.',
    };
  }

  return {
    precedence:
      "Your own rules come first, the most specific winning: a single product, then a product type, then your default. Your principal agency's rules apply only to sales none of yours covers.",
    defaultDescription:
      "Applies to everything you sell, unless a more specific rule of yours says otherwise. While you have one, none of your principal agency's rules apply — not even their rules for a product type or a single product.",
    noDefault:
      "No default of your own. Your principal agency's rules apply where they cover a sale; otherwise travellers pay the net price.",
    noTypeRule: hasOwnDefault
      ? 'Uses your default.'
      : "Uses your principal agency's rules, if any cover it.",
  };
}

// ----------------------------------------------------------------- the form

/** What the rule form holds: exactly what the agent typed, as text. */
export interface RuleDraft {
  calculationType: CalculationType;
  percent: string;
  amount: string;
  minCap: string;
  maxCap: string;
}

export type DraftErrors = Partial<Record<'percent' | 'amount' | 'minCap' | 'maxCap', string>>;

export function draftFromRule(rule?: MarkupRule): RuleDraft {
  const optionalAmount = (value: number | string | null) => {
    const minor = toOptionalWholeNumber(value);
    return minor === null ? '' : amountInputFromMinor(minor);
  };

  return {
    calculationType: rule?.calculationType === 'Fixed' ? 'Fixed' : 'Percentage',
    percent:
      rule?.percentBasisPoints !== null && rule?.percentBasisPoints !== undefined
        ? formatPercent(toWholeNumber(rule.percentBasisPoints))
        : '',
    amount: optionalAmount(rule?.valueMinor ?? null),
    minCap: optionalAmount(rule?.minMarkupMinor ?? null),
    maxCap: optionalAmount(rule?.maxMarkupMinor ?? null),
  };
}

/**
 * The request body for `draft` in `slot`, or the reasons it cannot be sent.
 *
 * The server checks all of this again and has the last word; checking here
 * only saves the agent a round trip to be told the minimum is above the maximum.
 */
export function buildRuleRequest(
  draft: RuleDraft,
  slot: RuleSlot,
  currency: string,
  replacing?: MarkupRule,
): { ok: true; request: MarkupRuleRequest } | { ok: false; errors: DraftErrors } {
  const errors: DraftErrors = {};
  let percentBasisPoints: number | null = null;
  let valueMinor: number | null = null;
  let minMarkupMinor: number | null = null;
  let maxMarkupMinor: number | null = null;

  if (draft.calculationType === 'Percentage') {
    const percent = parsePercent(draft.percent);
    if (percent.ok) percentBasisPoints = percent.value;
    else errors.percent = percent.error;

    // Caps are optional: blank means "no cap", not zero.
    if (draft.minCap.trim() !== '') {
      const min = parseAmount(draft.minCap);
      if (min.ok) minMarkupMinor = min.value;
      else errors.minCap = min.error;
    }
    if (draft.maxCap.trim() !== '') {
      const max = parseAmount(draft.maxCap);
      if (max.ok) maxMarkupMinor = max.value;
      else errors.maxCap = max.error;
    }
    if (minMarkupMinor !== null && maxMarkupMinor !== null && minMarkupMinor > maxMarkupMinor) {
      errors.maxCap = 'The maximum cannot be less than the minimum.';
    }
  } else {
    // A cap on a number that never moves would do nothing, so a fixed rule has none.
    const amount = parseAmount(draft.amount);
    if (amount.ok) valueMinor = amount.value;
    else errors.amount = amount.error;
  }

  if (Object.keys(errors).length > 0) return { ok: false, errors };

  return {
    ok: true,
    request: {
      scope: slot.scope,
      productType: slot.scope === 'Global' ? null : slot.productType,
      productId: slot.scope === 'Product' ? slot.productId : null,
      supplierCode: null,
      currency,
      calculationType: draft.calculationType,
      percentBasisPoints,
      valueMinor,
      minMarkupMinor,
      maxMarkupMinor,
      // Kept from the rule being replaced, so changing a percentage does not
      // quietly change who else it applies to or how it ranks.
      priority: replacing ? toWholeNumber(replacing.priority) : 0,
      appliesToSubAgents: replacing?.appliesToSubAgents ?? true,
      effectiveFrom: null,
      effectiveTo: null,
    },
  };
}
