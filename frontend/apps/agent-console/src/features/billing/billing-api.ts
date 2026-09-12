import { createContext, useContext } from 'react';
import type { Invoice, MyPlan, Plan, PlanChange } from './types';

/**
 * The agency's own subscription.
 *
 * **No card details pass through here and none can.** A first payment goes to Paystack's hosted
 * page and comes back as a reusable token; every renewal charges that token. There is deliberately
 * no method on this port that could accept a card number — build-plan decision 18, and the reason
 * we are in PCI SAQ-A.
 */
export interface BillingApi {
  /** The plans this agency may choose, priced in its own currency. */
  listPlans(): Promise<Plan[]>;

  /** The plan it is on now, with what it includes and what happens next. */
  getMyPlan(): Promise<MyPlan>;

  /** Its subscription invoices, newest first, each with the lines that add up to its total. */
  listInvoices(): Promise<Invoice[]>;

  /**
   * Chooses a plan.
   *
   * Three answers, and the caller has to handle all three: it is done, it needs paying for on the
   * gateway's page, or it has been scheduled for the end of the period already paid for.
   */
  choosePlan(tierId: string, callbackUrl: string): Promise<PlanChange>;

  /** Finishes a hosted-page payment with the reference the gateway sent the agency back with. */
  completeCheckout(reference: string): Promise<PlanChange>;

  /** Calls off a change the agency asked for and has thought better of. */
  cancelScheduledChange(migrationId: string): Promise<PlanChange>;
}

const BillingApiContext = createContext<BillingApi | null>(null);

export const BillingApiProvider = BillingApiContext.Provider;

export function useBillingApi(): BillingApi {
  const api = useContext(BillingApiContext);
  if (!api) throw new Error('useBillingApi must be used inside a <BillingApiProvider>.');
  return api;
}

export const billingKeys = {
  all: ['billing'] as const,
  plans: () => [...billingKeys.all, 'plans'] as const,
  myPlan: () => [...billingKeys.all, 'my-plan'] as const,
  invoices: () => [...billingKeys.all, 'invoices'] as const,
};
