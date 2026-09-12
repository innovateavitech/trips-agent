import type { BillingApi } from '../billing-api';
import type { Invoice, MyPlan, Plan, PlanChange } from '../types';

/**
 * A stand-in for demo mode (`VITE_AUTH_MODE=mock`), so the console can be shown without a backend.
 *
 * Never reached with a real sign-in — `main.tsx` chooses the HTTP adapter otherwise. It holds a
 * plan and two invoices in memory and goes no further than that: a stand-in that pretended to
 * charge a card would be a stand-in somebody eventually trusted.
 */
const FEATURES = [
  { code: 'max_sub_agents', name: 'Sub-agents', display: '5' },
  { code: 'custom_domain', name: 'Custom domain', display: 'on' },
  { code: 'transaction_fee_bps', name: 'Transaction fee', display: '1.5%' },
  { code: 'max_catalog_listings', name: 'Published listings', display: 'unlimited' },
  { code: 'loyalty_program', name: 'Loyalty programme', display: 'off' },
  { code: 'api_access', name: 'API access', display: 'off' },
];

const STARTER_FEATURES = [
  { code: 'max_sub_agents', name: 'Sub-agents', display: '0' },
  { code: 'custom_domain', name: 'Custom domain', display: 'off' },
  { code: 'transaction_fee_bps', name: 'Transaction fee', display: '2.5%' },
  { code: 'max_catalog_listings', name: 'Published listings', display: '10' },
  { code: 'loyalty_program', name: 'Loyalty programme', display: 'off' },
  { code: 'api_access', name: 'API access', display: 'off' },
];

let plan: MyPlan = {
  subscriptionId: 'demo-subscription',
  tierId: 'demo-growth',
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
  features: FEATURES,
  scheduledChange: null,
};

const PLANS: Plan[] = [
  {
    tierId: 'demo-starter',
    code: 'starter',
    name: 'Starter',
    description: 'For an agency finding its feet.',
    currency: 'NGN',
    amountMinor: 500_000,
    interval: 'Monthly',
    trialDays: 14,
    isCurrent: false,
    isFallback: false,
    features: STARTER_FEATURES,
  },
  {
    tierId: 'demo-growth',
    code: 'growth',
    name: 'Growth',
    description: 'Sub-agents, your own domain, and a lower fee.',
    currency: 'NGN',
    amountMinor: 2_500_000,
    interval: 'Monthly',
    trialDays: 0,
    isCurrent: true,
    isFallback: false,
    features: FEATURES,
  },
];

const INVOICES: Invoice[] = [
  {
    id: 'demo-invoice-2',
    invoiceNumber: 'TRIPS-INV-2026-000104',
    receiptNumber: 'TRIPS-RCT-2026-000091',
    status: 'Paid',
    statusReason: null,
    currency: 'NGN',
    totalMinor: 2_500_000,
    issuedAt: '2026-09-01T04:00:00Z',
    dueAt: '2026-09-01T04:00:00Z',
    paidAt: '2026-09-01T04:00:12Z',
    periodStart: '2026-09-01T00:00:00Z',
    periodEnd: '2026-10-01T00:00:00Z',
    lines: [
      {
        description: 'Growth plan — 1 Sep 2026 to 30 Sep 2026',
        quantity: 1,
        unitAmountMinor: 2_500_000,
        amountMinor: 2_500_000,
      },
    ],
  },
  {
    id: 'demo-invoice-1',
    invoiceNumber: 'TRIPS-INV-2026-000061',
    receiptNumber: 'TRIPS-RCT-2026-000054',
    status: 'Paid',
    statusReason: null,
    currency: 'NGN',
    totalMinor: 2_500_000,
    issuedAt: '2026-08-01T04:00:00Z',
    dueAt: '2026-08-01T04:00:00Z',
    paidAt: '2026-08-01T04:00:09Z',
    periodStart: '2026-08-01T00:00:00Z',
    periodEnd: '2026-09-01T00:00:00Z',
    lines: [
      {
        description: 'Growth plan — 1 Aug 2026 to 31 Aug 2026',
        quantity: 1,
        unitAmountMinor: 2_500_000,
        amountMinor: 2_500_000,
      },
    ],
  },
];

const DELAY_MS = 250;

function later<T>(value: T): Promise<T> {
  return new Promise((resolve) => setTimeout(() => resolve(value), DELAY_MS));
}

export const mockBillingApi: BillingApi = {
  listPlans: () => later(PLANS),
  getMyPlan: () => later(plan),
  listInvoices: () => later(INVOICES),

  choosePlan: (tierId) => {
    const chosen = PLANS.find((candidate) => candidate.tierId === tierId);

    if (!chosen) return later<PlanChange>({ result: 'applied', plan });

    // Cheaper is scheduled, dearer would be paid for — and the stand-in stops short of pretending
    // to take money, so it schedules either way and says so.
    const change: PlanChange = {
      result: 'scheduled',
      change: {
        migrationId: 'demo-migration',
        fromPlan: plan.planName,
        toPlan: chosen.name,
        reason: 'Downgrade',
        explanation:
          'Demo mode does not take payments. On the real console an upgrade would go to Paystack ' +
          'and a downgrade would be scheduled for the end of this period.',
        effectiveAt: '2026-10-01T00:00:00Z',
        canCancel: true,
      },
    };

    plan = { ...plan, scheduledChange: change.change };

    return later(change);
  },

  completeCheckout: () => later<PlanChange>({ result: 'applied', plan }),

  cancelScheduledChange: () => {
    plan = { ...plan, scheduledChange: null };
    return later<PlanChange>({ result: 'applied', plan });
  },
};
