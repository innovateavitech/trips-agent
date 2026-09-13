import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { Schemas } from '@trips/api-client';
import { int64 } from '@trips/utils';
import { api } from '../../api/client';
import { ApiError, unwrap } from '../../api/errors';
import type { WithdrawableBalance } from './payout-rules';

/**
 * Payouts and disputes talk to the real endpoints directly, like pricing does.
 * Everything sits under `['payouts']` so a request refreshes the balance, the
 * history and the wallet together.
 */
export const payoutKeys = {
  all: ['payouts'] as const,
  balance: () => [...payoutKeys.all, 'balance'] as const,
  accounts: () => [...payoutKeys.all, 'accounts'] as const,
  banks: () => [...payoutKeys.all, 'banks'] as const,
  history: () => [...payoutKeys.all, 'history'] as const,
  disputes: () => ['disputes'] as const,
  dispute: (id: string) => ['disputes', id] as const,
};

export type BankAccount = Schemas['BankAccountResponse'];
export type Bank = Schemas['BankResponse'];

export interface Payout {
  id: string;
  reference: string;
  amountMinor: number;
  currency: string;
  status: string;
  bankName: string;
  maskedNumber: string;
  requestedAt: string;
  completedAt: string | null;
  rejectionReason: string | null;
  failureReason: string | null;
}

export interface Dispute {
  id: string;
  paymentReference: string;
  amountMinor: number;
  currency: string;
  category: string | null;
  reason: string | null;
  status: string;
  holdOutcome: string;
  openedAt: string;
  evidenceDueAt: string;
  evidenceSubmittedAt: string | null;
  resolvedAt: string | null;
  acceptsEvidence: boolean;
  evidenceDefaults: Schemas['DisputeEvidenceDefaults'] | null;
}

function toDispute(raw: Schemas['DisputeResponse']): Dispute {
  return {
    id: raw.id,
    paymentReference: raw.paymentReference,
    amountMinor: int64(raw.amountMinor),
    currency: raw.currency,
    category: raw.category ?? null,
    reason: raw.reason ?? null,
    status: raw.status,
    holdOutcome: raw.holdOutcome,
    openedAt: raw.openedAt,
    evidenceDueAt: raw.evidenceDueAt,
    evidenceSubmittedAt: raw.evidenceSubmittedAt ?? null,
    resolvedAt: raw.resolvedAt ?? null,
    acceptsEvidence: raw.acceptsEvidence,
    evidenceDefaults: raw.evidenceDefaults ?? null,
  };
}

export function useWithdrawableBalance() {
  return useQuery({
    queryKey: payoutKeys.balance(),
    queryFn: async (): Promise<WithdrawableBalance> => {
      const raw = await unwrap(api.GET('/api/v1/payouts/balance'));
      return {
        balanceMinor: int64(raw.balanceMinor),
        reservedMinor: int64(raw.reservedMinor),
        availableMinor: int64(raw.availableMinor),
        pendingSettlementMinor: int64(raw.pendingSettlementMinor),
        withdrawableMinor: int64(raw.withdrawableMinor),
        minimumPayoutMinor: int64(raw.minimumPayoutMinor),
        dailyCapMinor: int64(raw.dailyCapMinor),
        settlementWindowDays: int64(raw.settlementWindowDays),
        currency: raw.currency,
      };
    },
  });
}

export function useBankAccounts() {
  return useQuery({
    queryKey: payoutKeys.accounts(),
    queryFn: () => unwrap(api.GET('/api/v1/payouts/bank-accounts')),
  });
}

export function useBanks(enabled: boolean) {
  return useQuery({
    queryKey: payoutKeys.banks(),
    queryFn: () => unwrap(api.GET('/api/v1/payouts/banks')),
    enabled,
    staleTime: 60 * 60 * 1000,
  });
}

export function usePayouts() {
  return useQuery({
    queryKey: payoutKeys.history(),
    queryFn: async (): Promise<Payout[]> =>
      (await unwrap(api.GET('/api/v1/payouts'))).map((raw) => ({
        id: raw.id,
        reference: raw.reference,
        amountMinor: int64(raw.amountMinor),
        currency: raw.currency,
        status: raw.status,
        bankName: raw.bankName,
        maskedNumber: raw.maskedNumber,
        requestedAt: raw.requestedAt,
        completedAt: raw.completedAt ?? null,
        rejectionReason: raw.rejectionReason ?? null,
        failureReason: raw.failureReason ?? null,
      })),
  });
}

export function useAddBankAccount() {
  const client = useQueryClient();

  return useMutation({
    mutationFn: (body: Schemas['AddBankAccountRequest']) =>
      unwrap(api.POST('/api/v1/payouts/bank-accounts', { body })),
    onSettled: () => client.invalidateQueries({ queryKey: payoutKeys.accounts() }),
  });
}

export function useMakeDefaultAccount() {
  const client = useQueryClient();

  return useMutation({
    mutationFn: async (bankAccountId: string) => {
      const result = await api.POST('/api/v1/payouts/bank-accounts/{bankAccountId}/default', {
        params: { path: { bankAccountId } },
      });
      if (result.error !== undefined || !result.response.ok) {
        throw ApiError.from(result.response, result.error);
      }
    },
    onSettled: () => client.invalidateQueries({ queryKey: payoutKeys.accounts() }),
  });
}

export function useRequestPayout() {
  const client = useQueryClient();

  return useMutation({
    mutationFn: (body: { amountMinor: number; bankAccountId: string | null }) =>
      unwrap(api.POST('/api/v1/payouts', { body })),
    onSettled: () =>
      Promise.all([
        client.invalidateQueries({ queryKey: payoutKeys.all }),
        client.invalidateQueries({ queryKey: ['wallet'] }),
      ]),
  });
}

export function useDisputes() {
  return useQuery({
    queryKey: payoutKeys.disputes(),
    queryFn: async () => (await unwrap(api.GET('/api/v1/disputes'))).map(toDispute),
  });
}

export function useDispute(id: string) {
  return useQuery({
    queryKey: payoutKeys.dispute(id),
    queryFn: async () =>
      toDispute(
        await unwrap(
          api.GET('/api/v1/disputes/{disputeId}', { params: { path: { disputeId: id } } }),
        ),
      ),
  });
}

export function useSubmitEvidence(id: string) {
  const client = useQueryClient();

  return useMutation({
    mutationFn: async (body: Schemas['SubmitDisputeEvidenceRequest']) => {
      // 204 on success, which unwrap would read as a broken contract.
      const result = await api.POST('/api/v1/disputes/{disputeId}/evidence', {
        params: { path: { disputeId: id } },
        body,
      });
      if (result.error !== undefined || !result.response.ok) {
        throw ApiError.from(result.response, result.error);
      }
    },
    onSettled: () => client.invalidateQueries({ queryKey: payoutKeys.disputes() }),
  });
}
