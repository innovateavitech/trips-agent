/**
 * The agency's own plan, as its console sees it.
 *
 * Money is always a whole number of minor units — kobo for NGN — and divided by 100 only at the
 * moment it is drawn. Percentages are basis points, where 100 is one per cent.
 */

export type SubscriptionStatus = 'Trialing' | 'Active' | 'PastDue' | 'Cancelled' | 'Expired';

export type InvoiceStatus = 'Open' | 'Paid' | 'PastDue' | 'Uncollectible' | 'Void';

/** One line of a plan's "what you get" list, already worded by the server. */
export interface PlanFeature {
  code: string;
  name: string;
  /** "on", "off", "25", "unlimited", "1.5%". */
  display: string;
}

export interface Plan {
  tierId: string;
  code: string;
  name: string;
  description: string | null;
  currency: string;
  /** Null for a plan with no price in this agency's currency. */
  amountMinor: number | null;
  interval: string;
  trialDays: number;
  isCurrent: boolean;
  isFallback: boolean;
  features: PlanFeature[];
}

/** A plan change that has been agreed and has not landed yet. */
export interface ScheduledChange {
  migrationId: string;
  fromPlan: string;
  toPlan: string;
  /** `Upgrade`, `Downgrade`, `AdminMigration` or `DunningFallback`. */
  reason: string;
  explanation: string;
  effectiveAt: string;
  /** False for a change Trips made: the agency cannot cancel that one here. */
  canCancel: boolean;
}

export interface MyPlan {
  subscriptionId: string | null;
  tierId: string | null;
  planName: string;
  status: SubscriptionStatus | null;
  statusReason: string | null;
  currency: string;
  amountMinor: number | null;
  interval: string | null;
  currentPeriodStart: string | null;
  currentPeriodEnd: string | null;
  trialEndsAt: string | null;
  nextChargeAt: string | null;
  /** "Visa •••• 4242", or null when there is no card to charge. */
  cardOnFile: string | null;
  dunningRetries: number;
  nextDunningAttemptAt: string | null;
  features: PlanFeature[];
  scheduledChange: ScheduledChange | null;
}

export interface InvoiceLine {
  description: string;
  quantity: number;
  unitAmountMinor: number;
  amountMinor: number;
}

/** `totalMinor` always equals the sum of the lines. The database enforces it. */
export interface Invoice {
  id: string;
  invoiceNumber: string;
  receiptNumber: string | null;
  status: InvoiceStatus;
  statusReason: string | null;
  currency: string;
  totalMinor: number;
  issuedAt: string;
  dueAt: string;
  paidAt: string | null;
  periodStart: string;
  periodEnd: string;
  lines: InvoiceLine[];
}

/**
 * What happened when the agency chose a plan.
 *
 *   applied           done, nothing to pay
 *   payment-required  send them to `authorizationUrl` — card entry happens only there
 *   scheduled         agreed for later; `scheduledChange` says when
 */
export type PlanChange =
  | { result: 'applied'; plan: MyPlan }
  | { result: 'payment-required'; authorizationUrl: string; reference: string; amountMinor: number }
  | { result: 'scheduled'; change: ScheduledChange };
