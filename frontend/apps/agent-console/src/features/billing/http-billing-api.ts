import type { ApiClient } from '@trips/api-client';
import { unwrap } from '../../api/errors';
import type { BillingApi } from './billing-api';
import type { Invoice, MyPlan, Plan, PlanChange, ScheduledChange } from './types';

/**
 * The billing screens against the real API (issues 64 and 65).
 *
 *   listPlans        → GET    /api/v1/billing/plans
 *   getMyPlan        → GET    /api/v1/billing/subscription
 *   listInvoices     → GET    /api/v1/billing/invoices
 *   choosePlan       → POST   /api/v1/billing/subscription
 *   completeCheckout → POST   /api/v1/billing/subscription/complete
 *   cancelScheduled  → DELETE /api/v1/billing/subscription/scheduled-change/{id}
 *
 * The generated client types most fields as optional, so everything is mapped into the shapes
 * above with explicit defaults rather than passed through — a screen reading `undefined` where it
 * expected a number would draw a blank where an amount belongs.
 */
export function createHttpBillingApi({ api }: { api: ApiClient }): BillingApi {
  return {
    async listPlans() {
      return (await unwrap(api.GET('/api/v1/billing/plans'))).map(toPlan);
    },

    async getMyPlan() {
      return toMyPlan(await unwrap(api.GET('/api/v1/billing/subscription')));
    },

    async listInvoices() {
      return (await unwrap(api.GET('/api/v1/billing/invoices'))).map(toInvoice);
    },

    async choosePlan(tierId, callbackUrl) {
      return toChange(
        await unwrap(api.POST('/api/v1/billing/subscription', { body: { tierId, callbackUrl } })),
      );
    },

    async completeCheckout(reference) {
      return toChange(
        await unwrap(api.POST('/api/v1/billing/subscription/complete', { body: { reference } })),
      );
    },

    async cancelScheduledChange(migrationId) {
      return toChange(
        await unwrap(
          api.DELETE('/api/v1/billing/subscription/scheduled-change/{migrationId}', {
            params: { path: { migrationId } },
          }),
        ),
      );
    },
  };
}

/* eslint-disable @typescript-eslint/no-explicit-any -- the generated types are all-optional; each
   field is defaulted explicitly below rather than trusted. */

function toPlan(raw: any): Plan {
  return {
    tierId: raw.tierId ?? '',
    code: raw.code ?? '',
    name: raw.name ?? 'Plan',
    description: raw.description ?? null,
    currency: raw.currency ?? 'NGN',
    amountMinor: raw.amountMinor ?? null,
    interval: raw.interval ?? 'Monthly',
    trialDays: raw.trialDays ?? 0,
    isCurrent: raw.isCurrent ?? false,
    isFallback: raw.isFallback ?? false,
    features: (raw.features ?? []).map(toFeature),
  };
}

function toFeature(raw: any) {
  return { code: raw.code ?? '', name: raw.name ?? '', display: raw.display ?? '' };
}

function toMyPlan(raw: any): MyPlan {
  return {
    subscriptionId: raw.subscriptionId ?? null,
    tierId: raw.tierId ?? null,
    planName: raw.planName ?? 'No plan',
    status: raw.status ?? null,
    statusReason: raw.statusReason ?? null,
    currency: raw.currency ?? 'NGN',
    amountMinor: raw.amountMinor ?? null,
    interval: raw.interval ?? null,
    currentPeriodStart: raw.currentPeriodStart ?? null,
    currentPeriodEnd: raw.currentPeriodEnd ?? null,
    trialEndsAt: raw.trialEndsAt ?? null,
    nextChargeAt: raw.nextChargeAt ?? null,
    cardOnFile: raw.cardOnFile ?? null,
    dunningRetries: raw.dunningRetries ?? 0,
    nextDunningAttemptAt: raw.nextDunningAttemptAt ?? null,
    features: (raw.features ?? []).map(toFeature),
    scheduledChange: raw.scheduledChange ? toScheduledChange(raw.scheduledChange) : null,
  };
}

function toScheduledChange(raw: any): ScheduledChange {
  return {
    migrationId: raw.migrationId ?? '',
    fromPlan: raw.fromPlan ?? '',
    toPlan: raw.toPlan ?? '',
    reason: raw.reason ?? '',
    explanation: raw.explanation ?? '',
    effectiveAt: raw.effectiveAt ?? '',
    canCancel: raw.canCancel ?? false,
  };
}

function toInvoice(raw: any): Invoice {
  return {
    id: raw.id ?? '',
    invoiceNumber: raw.invoiceNumber ?? '',
    receiptNumber: raw.receiptNumber ?? null,
    status: raw.status ?? 'Open',
    statusReason: raw.statusReason ?? null,
    currency: raw.currency ?? 'NGN',
    totalMinor: raw.totalMinor ?? 0,
    issuedAt: raw.issuedAt ?? '',
    dueAt: raw.dueAt ?? '',
    paidAt: raw.paidAt ?? null,
    periodStart: raw.periodStart ?? '',
    periodEnd: raw.periodEnd ?? '',
    lines: (raw.lines ?? []).map((line: any) => ({
      description: line.description ?? '',
      quantity: line.quantity ?? 1,
      unitAmountMinor: line.unitAmountMinor ?? 0,
      amountMinor: line.amountMinor ?? 0,
    })),
  };
}

function toChange(raw: any): PlanChange {
  if (raw.result === 'PaymentRequired') {
    return {
      result: 'payment-required',
      authorizationUrl: raw.authorizationUrl ?? '',
      reference: raw.reference ?? '',
      amountMinor: raw.amountMinor ?? 0,
    };
  }

  if (raw.result === 'Scheduled') {
    return { result: 'scheduled', change: toScheduledChange(raw.scheduledChange ?? {}) };
  }

  return { result: 'applied', plan: toMyPlan(raw.plan ?? {}) };
}
