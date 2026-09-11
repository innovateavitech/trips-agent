import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../../api/client';
import { unwrap } from '../../api/errors';
import {
  toWholeNumber,
  type MarkupRule,
  type MarkupRuleRequest,
  type ProductType,
} from './pricing-rules';

/**
 * Query keys. Everything sits under `['pricing']`, so one invalidation after a
 * rule change refreshes the list *and* re-runs the "which rule wins" preview —
 * the agent sees their change take effect straight away.
 */
export const pricingKeys = {
  all: ['pricing'] as const,
  settings: () => [...pricingKeys.all, 'settings'] as const,
  rules: () => [...pricingKeys.all, 'rules'] as const,
  preview: (input: PreviewInput) => [...pricingKeys.all, 'preview', input] as const,
};

export interface PricingSettings {
  currency: string;
  vatRateBasisPoints: number;
  platformFeeBasisPoints: number;
  quoteValidityMinutes: number;
}

export function usePricingSettings() {
  return useQuery({
    queryKey: pricingKeys.settings(),
    queryFn: async (): Promise<PricingSettings> => {
      const raw = await unwrap(api.GET('/api/v1/pricing/settings'));
      return {
        currency: raw.currency,
        vatRateBasisPoints: toWholeNumber(raw.vatRateBasisPoints),
        platformFeeBasisPoints: toWholeNumber(raw.platformFeeBasisPoints),
        quoteValidityMinutes: toWholeNumber(raw.quoteValidityMinutes),
      };
    },
    // An agency's currency and VAT rate do not change during a session.
    staleTime: Infinity,
  });
}

export function useMarkupRules() {
  return useQuery({
    queryKey: pricingKeys.rules(),
    queryFn: () => unwrap(api.GET('/api/v1/pricing/markup-rules')),
  });
}

/**
 * Saves a rule. With `replacing`, it is a PUT — which the server turns into
 * "retire the old rule, start a new one", never an in-place edit. That is what
 * lets the screen promise that past quotes and bookings are untouched.
 */
export function useSaveRule() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: ({ request, replacing }: { request: MarkupRuleRequest; replacing?: MarkupRule }) =>
      replacing
        ? unwrap(
            api.PUT('/api/v1/pricing/markup-rules/{ruleId}', {
              params: { path: { ruleId: replacing.id } },
              body: request,
            }),
          )
        : unwrap(api.POST('/api/v1/pricing/markup-rules', { body: request })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: pricingKeys.all }),
  });
}

/** Stops a rule applying from now on. It stays in the history. */
export function useRetireRule() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (ruleId: string) =>
      unwrap(
        api.POST('/api/v1/pricing/markup-rules/{ruleId}/retire', {
          params: { path: { ruleId } },
        }),
      ),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: pricingKeys.all }),
  });
}

export interface PreviewInput {
  productType: ProductType;
  productId: string | null;
  netAmountMinor: number;
}

export interface PricePreview {
  netAmountMinor: number;
  markupAmountMinor: number;
  vatRateBasisPoints: number;
  taxAmountMinor: number;
  platformFeeBasisPoints: number;
  platformFeeMinor: number;
  agentMarginMinor: number;
  grossAmountMinor: number;
  winningRule: (MarkupRule & { inherited: boolean }) | null;
}

/**
 * What the server says a sample net price sells for, and which rule decided.
 *
 * Nothing is stored by this call — the server prices without writing a quote —
 * so the agent can try as many figures as they like.
 */
export function usePricePreview(input: PreviewInput | null) {
  return useQuery({
    queryKey: pricingKeys.preview(
      input ?? { productType: 'Flight', productId: null, netAmountMinor: 0 },
    ),
    queryFn: async (): Promise<PricePreview> => {
      const raw = await unwrap(
        api.POST('/api/v1/pricing/preview', {
          body: {
            productType: (input as PreviewInput).productType,
            productId: (input as PreviewInput).productId,
            supplierCode: null,
            currency: null,
            netAmountMinor: (input as PreviewInput).netAmountMinor,
          },
        }),
      );

      const winner = raw.winningRule;

      return {
        netAmountMinor: toWholeNumber(raw.netAmountMinor),
        markupAmountMinor: toWholeNumber(raw.markupAmountMinor),
        vatRateBasisPoints: toWholeNumber(raw.vatRateBasisPoints),
        taxAmountMinor: toWholeNumber(raw.taxAmountMinor),
        platformFeeBasisPoints: toWholeNumber(raw.platformFeeBasisPoints),
        platformFeeMinor: toWholeNumber(raw.platformFeeMinor),
        agentMarginMinor: toWholeNumber(raw.agentMarginMinor),
        grossAmountMinor: toWholeNumber(raw.grossAmountMinor),
        winningRule: winner
          ? {
              ...winner,
              currency: raw.currency,
              appliesToSubAgents: true,
              effectiveFrom: '',
              effectiveTo: null,
              supersededById: null,
            }
          : null,
      };
    },
    enabled: input !== null,
    // Keep the last answer on screen while the next one loads, so the numbers
    // change in place as the agent types instead of flashing a spinner.
    placeholderData: keepPreviousData,
  });
}
