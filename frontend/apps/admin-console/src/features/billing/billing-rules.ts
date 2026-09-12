import { formatBasisPoints } from '../../lib/format';
import type {
  EntitlementCatalogueItem,
  EntitlementValueType,
  Subscriber,
  SubscriptionStatus,
  Tier,
  TierStatus,
} from './types';

/** How a status reads, and what it means — never just a coloured word. */
export interface StatusDisplay {
  label: string;
  tone: 'neutral' | 'primary' | 'success' | 'warning' | 'destructive' | 'info';
  meaning: string;
}

export function tierStatusDisplay(status: TierStatus): StatusDisplay {
  if (status === 'Published') {
    return { label: 'Published', tone: 'success', meaning: 'Agencies can subscribe to it.' };
  }

  if (status === 'Archived') {
    return {
      label: 'Archived',
      tone: 'neutral',
      meaning: 'Retired. Agencies already on it keep it; nobody new can join.',
    };
  }

  return { label: 'Draft', tone: 'warning', meaning: 'Nobody outside this console can see it.' };
}

export function subscriptionStatusDisplay(status: SubscriptionStatus): StatusDisplay {
  switch (status) {
    case 'Active':
      return { label: 'Active', tone: 'success', meaning: 'Paid up to the end of the period.' };
    case 'Trialing':
      return {
        label: 'Trialing',
        tone: 'info',
        meaning: 'Inside a free trial. Nothing charged yet.',
      };
    case 'PastDue':
      return {
        label: 'Past due',
        tone: 'warning',
        meaning: 'A charge failed and the retry schedule is running. The plan still applies.',
      };
    case 'Cancelled':
      return {
        label: 'Cancelled',
        tone: 'destructive',
        meaning: 'Ended. The plan grants nothing.',
      };
    default:
      return { label: 'Expired', tone: 'neutral', meaning: 'Ran out with nothing to renew it.' };
  }
}

/**
 * Whether a tier can be deleted, and what to say when it cannot.
 *
 * Deleting is almost always the wrong answer and the server refuses it anyway. Saying so here means
 * the admin reads the reason before clicking rather than after.
 */
export function deletability(tier: Tier): { canDelete: boolean; reason: string } {
  if (tier.subscribers > 0) {
    const plural = tier.subscribers === 1 ? 'agency is' : 'agencies are';

    return {
      canDelete: false,
      reason:
        `${tier.subscribers} ${plural} on this plan, so it cannot be deleted. Archive it instead: ` +
        'they keep the plan and nobody new can join.',
    };
  }

  if (tier.status !== 'Draft' || tier.publishedAt !== null) {
    return {
      canDelete: false,
      reason:
        'This plan has been published, so it cannot be deleted even with nobody on it. Archive it ' +
        'instead — invoices and reports name the plan they billed for.',
    };
  }

  return { canDelete: true, reason: 'A draft nobody has ever seen. Deleting it loses nothing.' };
}

/** Whether a tier can be published, and why not when it cannot. */
export function publishability(tier: Tier): { canPublish: boolean; reason: string } {
  if (tier.status === 'Published') {
    return { canPublish: false, reason: 'It is already published.' };
  }

  if (tier.status === 'Archived') {
    return { canPublish: false, reason: 'Restore it first — archiving is deliberate.' };
  }

  if (!tier.isFallback && currentPrice(tier) === null) {
    return {
      canPublish: false,
      reason: 'Give it a price first. A published plan with no price cannot be charged for.',
    };
  }

  return { canPublish: true, reason: 'Agencies will be able to subscribe to it.' };
}

/** The price being charged today, or null when the tier has none. */
export function currentPrice(tier: Tier) {
  return tier.prices.find((price) => price.effectiveTo === null) ?? null;
}

/**
 * Is what an admin typed a usable value for this entitlement?
 *
 * Returns the JSON scalar to send, or a sentence saying what is wrong. The backend checks the same
 * thing and the database checks it again; this is only so the admin is told before they submit.
 */
export function parseEntitlementValue(
  valueType: EntitlementValueType,
  raw: string,
): { value: string } | { problem: string } {
  const text = raw.trim();

  if (valueType === 'Flag') {
    if (text === 'true' || text === 'false') return { value: text };
    return { problem: 'A yes/no setting is either on or off.' };
  }

  if (!/^-?\d+$/.test(text)) {
    return { problem: 'A whole number, with no decimal point.' };
  }

  const number = Number.parseInt(text, 10);

  if (valueType === 'Limit') {
    if (number < -1) return { problem: 'A limit is -1 for unlimited, or zero and above.' };
    return { value: String(number) };
  }

  if (number < 0 || number > 10_000) {
    return { problem: 'A fee is 0 to 10,000 basis points — 10,000 is one hundred per cent.' };
  }

  return { value: String(number) };
}

/** How a stored entitlement value reads to a person: "on", "25", "unlimited", "1.5%". */
export function describeEntitlementValue(valueType: EntitlementValueType, value: string): string {
  const text = value.trim();

  if (valueType === 'Flag') return text === 'true' ? 'on' : 'off';

  const number = Number.parseInt(text, 10);

  if (Number.isNaN(number)) return text;
  if (valueType === 'Limit') return number === -1 ? 'unlimited' : String(number);

  return formatBasisPoints(number);
}

/**
 * What a tier grants, filled in from the catalogue.
 *
 * An entitlement a tier does not grant is not missing from the plan — it falls back to the
 * catalogue's default, which is always the restrictive answer. Showing a blank row instead would
 * leave an admin guessing which way the blank goes.
 */
export function grantsOf(
  tier: Tier,
  catalogue: EntitlementCatalogueItem[],
): {
  code: string;
  name: string;
  valueType: EntitlementValueType;
  value: string;
  granted: boolean;
}[] {
  return catalogue.map((item) => {
    const granted = tier.entitlements.find((entitlement) => entitlement.code === item.code);

    return {
      code: item.code,
      name: item.name,
      valueType: item.valueType,
      value: granted?.value ?? item.defaultValue,
      granted: granted !== undefined,
    };
  });
}

/** A one-line summary of a subscriber's dunning state, or null when nothing is failing. */
export function dunningSummary(subscriber: Subscriber): string | null {
  if (subscriber.status !== 'PastDue') return null;

  const made = subscriber.dunningRetries;
  const left = Math.max(0, 4 - made);

  if (left === 0) {
    return 'Every retry has been used. The next run moves them to the free plan, or suspends them.';
  }

  return `${made} of 4 retries made; ${left} left.`;
}
