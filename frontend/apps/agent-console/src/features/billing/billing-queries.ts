import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { billingKeys, useBillingApi } from './billing-api';

export function usePlans() {
  const api = useBillingApi();

  return useQuery({
    queryKey: billingKeys.plans(),
    queryFn: () => api.listPlans(),
    // The plans on offer change when Trips changes them, which is rarely and never while somebody
    // is looking at the picker.
    staleTime: 5 * 60 * 1000,
  });
}

export function useMyPlan() {
  const api = useBillingApi();

  return useQuery({
    queryKey: billingKeys.myPlan(),
    queryFn: () => api.getMyPlan(),
  });
}

export function useInvoices() {
  const api = useBillingApi();

  return useQuery({
    queryKey: billingKeys.invoices(),
    queryFn: () => api.listInvoices(),
  });
}

/**
 * After any change — success, refusal or a lost connection — ask the server again.
 *
 * Never patched by hand. What an agency is on, and what it owes, is the server's answer; showing
 * the change somebody *meant* to make would be worst exactly when it matters, after a timeout.
 */
function useInvalidate() {
  const queryClient = useQueryClient();
  return () => queryClient.invalidateQueries({ queryKey: billingKeys.all });
}

export function useChoosePlan() {
  const api = useBillingApi();
  const invalidate = useInvalidate();

  return useMutation({
    mutationFn: ({ tierId, callbackUrl }: { tierId: string; callbackUrl: string }) =>
      api.choosePlan(tierId, callbackUrl),
    onSettled: invalidate,
  });
}

/**
 * Finishes a payment the agency made on Paystack's page.
 *
 * Never retried automatically, and it does not need to be: the server asks the gateway what
 * actually happened rather than trusting the redirect, and asking twice about a settled payment is
 * harmless. What would not be harmless is treating a slow answer as a failure and inviting the
 * agency to pay again.
 */
export function useCompleteCheckout() {
  const api = useBillingApi();
  const invalidate = useInvalidate();

  return useMutation({
    mutationFn: (reference: string) => api.completeCheckout(reference),
    onSettled: invalidate,
  });
}

export function useCancelScheduledChange() {
  const api = useBillingApi();
  const invalidate = useInvalidate();

  return useMutation({
    mutationFn: (migrationId: string) => api.cancelScheduledChange(migrationId),
    onSettled: invalidate,
  });
}
