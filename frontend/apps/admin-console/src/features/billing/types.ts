/**
 * The shapes the billing endpoints return.
 *
 * Hand-written mirrors of `TripsAgent.Contracts.Billing`, like every other feature in this console
 * — see the README's note about `@trips/api-client`. Money is always a whole number of minor units
 * and never a decimal; percentages are basis points, where 100 is one percent.
 */

/** `Flag` is yes/no, `Limit` is a ceiling (-1 means unlimited), `Rate` is basis points. */
export type EntitlementValueType = 'Flag' | 'Limit' | 'Rate';

export type TierStatus = 'Draft' | 'Published' | 'Archived';

export type BillingInterval = 'Monthly' | 'Annual';

export type SubscriptionStatus = 'Trialing' | 'Active' | 'PastDue' | 'Cancelled' | 'Expired';

/** One thing a tier can grant, from the catalogue fixed in the backend's code. */
export interface EntitlementCatalogueItem {
  code: string;
  name: string;
  description: string;
  valueType: EntitlementValueType;
  /** The JSON scalar an agency with no plan gets: `false`, `0`, `-1`. */
  defaultValue: string;
}

export interface TierPrice {
  id: string;
  currency: string;
  interval: BillingInterval;
  amountMinor: number;
  isPromotional: boolean;
  effectiveFrom: string;
  /** Null while it is the price being charged. */
  effectiveTo: string | null;
}

export interface TierEntitlement {
  code: string;
  name: string;
  valueType: EntitlementValueType;
  /** The JSON scalar stored against the tier. */
  value: string;
  /** The same value for a person: "on", "25", "unlimited", "1.5%". */
  display: string;
}

export interface Tier {
  id: string;
  code: string;
  name: string;
  customerDescription: string | null;
  status: TierStatus;
  trialDays: number;
  sortOrder: number;
  isFallback: boolean;
  /** Non-zero means the tier can only be archived, never deleted. */
  subscribers: number;
  publishedAt: string | null;
  archivedAt: string | null;
  prices: TierPrice[];
  entitlements: TierEntitlement[];
}

export interface SaveTierRequest {
  code: string;
  name: string;
  customerDescription: string | null;
  trialDays: number;
  sortOrder: number;
  isFallback: boolean;
  reason: string;
}

export interface SetTierPriceRequest {
  currency: string;
  interval: BillingInterval;
  amountMinor: number;
  reason: string;
}

export interface SetTierEntitlementsRequest {
  entitlements: { code: string; value: string }[];
  reason: string;
}

export interface TierReasonRequest {
  reason: string;
}

export interface MigrateSubscribersRequest {
  toTierId: string;
  reason: string;
}

export interface TierChangeResponse {
  tier: Tier;
  /** How many agencies were told they are moving. Zero for most changes. */
  subscribersScheduled: number;
}

export interface Subscriber {
  agencyId: string;
  agencyName: string;
  agencyStatus: string;
  subscriptionId: string;
  tierName: string;
  status: SubscriptionStatus;
  currency: string;
  amountMinor: number | null;
  currentPeriodStart: string;
  currentPeriodEnd: string;
  trialEndsAt: string | null;
  dunningRetries: number;
  nextDunningAttemptAt: string | null;
  /** What this agency owes Trips right now, in minor units. */
  outstandingMinor: number;
}
