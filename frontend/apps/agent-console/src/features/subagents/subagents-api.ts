import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createContext, useContext } from 'react';
import { useCurrentUser } from '../../auth/auth-provider';
import type {
  Allowance,
  AllowancePeriod,
  InviteSubAgent,
  NetworkPerformance,
  SellableProductType,
  SubAgentInvited,
  SubAgentNetwork,
  SubAgentPermission,
  SubAgentScope,
} from './types';

/**
 * Everything the sub-agent screens need from the server — the same
 * port-and-adapter shape as `BookingsApi` and `WalletApi`. `main.tsx` chooses
 * the adapter; the stand-in in `mock/` is only reached in demo mode.
 */
export interface SubAgentsApi {
  /** The principal's sub-agents, and how much room its plan leaves. */
  listSubAgents(): Promise<SubAgentNetwork>;

  /** Creates the agency and invites the person who will run it, in one call. */
  invite(request: InviteSubAgent): Promise<SubAgentInvited>;

  /** Stops it selling. It can still sign in and read — it has travellers to look after. */
  freeze(id: string, reason: string): Promise<void>;

  unfreeze(id: string, reason: string): Promise<void>;

  /** Ends the relationship. Its bookings stay readable; its sessions stop working. */
  revoke(id: string, reason: string): Promise<void>;

  listScopes(id: string): Promise<SubAgentScope[]>;

  grantScope(
    id: string,
    productType: SellableProductType,
    supplierId: string | null,
  ): Promise<void>;

  revokeScope(id: string, scopeId: string): Promise<void>;

  listPermissions(id: string): Promise<SubAgentPermission[]>;

  denyPermission(id: string, code: string, reason: string): Promise<void>;

  allowPermission(id: string, code: string): Promise<void>;

  /** Null when the sub-agent has no allowance, and so may spend nothing. */
  getAllowance(id: string): Promise<Allowance | null>;

  setAllowance(id: string, limitMinor: number, period: AllowancePeriod): Promise<Allowance>;

  freezeAllowance(id: string): Promise<Allowance>;

  unfreezeAllowance(id: string): Promise<Allowance>;

  /** Consolidated figures across the network, for a date range. */
  networkPerformance(from: string, to: string): Promise<NetworkPerformance>;
}

const SubAgentsApiContext = createContext<SubAgentsApi | null>(null);

export const SubAgentsApiProvider = SubAgentsApiContext.Provider;

function useSubAgentsApi(): SubAgentsApi {
  const api = useContext(SubAgentsApiContext);
  if (!api) throw new Error('useSubAgentsApi must be used inside a <SubAgentsApiProvider>.');
  return api;
}

/** Keyed by agency, so one agency's network is never shown to the next. */
export const subAgentKeys = {
  all: ['sub-agents'] as const,
  list: (agencyId: string) => [...subAgentKeys.all, 'list', agencyId] as const,
  scopes: (agencyId: string, id: string) => [...subAgentKeys.all, 'scopes', agencyId, id] as const,
  permissions: (agencyId: string, id: string) =>
    [...subAgentKeys.all, 'permissions', agencyId, id] as const,
  allowance: (agencyId: string, id: string) =>
    [...subAgentKeys.all, 'allowance', agencyId, id] as const,
  performance: (agencyId: string, from: string, to: string) =>
    [...subAgentKeys.all, 'performance', agencyId, from, to] as const,
};

function useAgencyId(): string {
  return useCurrentUser().agency?.id ?? 'none';
}

export function useSubAgents() {
  const api = useSubAgentsApi();

  return useQuery({
    queryKey: subAgentKeys.list(useAgencyId()),
    queryFn: () => api.listSubAgents(),
  });
}

export function useInviteSubAgent() {
  const api = useSubAgentsApi();
  const agencyId = useAgencyId();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (request: InviteSubAgent) => api.invite(request),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: subAgentKeys.list(agencyId) });
    },
  });
}

/** Freeze, unfreeze and revoke, which all take a reason and all change the list. */
export function useChangeStanding() {
  const api = useSubAgentsApi();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: ({
      id,
      action,
      reason,
    }: {
      id: string;
      action: 'freeze' | 'unfreeze' | 'revoke';
      reason: string;
    }) => {
      if (action === 'freeze') return api.freeze(id, reason);
      if (action === 'unfreeze') return api.unfreeze(id, reason);
      return api.revoke(id, reason);
    },
    onSuccess: () => {
      // Revoking zeroes the allowance too, so the whole feature is refreshed.
      void queryClient.invalidateQueries({ queryKey: subAgentKeys.all });
    },
  });
}

export function useSubAgentScopes(id: string) {
  const api = useSubAgentsApi();

  return useQuery({
    queryKey: subAgentKeys.scopes(useAgencyId(), id),
    queryFn: () => api.listScopes(id),
  });
}

export function useChangeScope(id: string) {
  const api = useSubAgentsApi();
  const agencyId = useAgencyId();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (
      change: { grant: SellableProductType; supplierId: string | null } | { revoke: string },
    ) =>
      'revoke' in change
        ? api.revokeScope(id, change.revoke)
        : api.grantScope(id, change.grant, change.supplierId),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: subAgentKeys.scopes(agencyId, id) });

      // The list shows how many scopes each sub-agent has, so it moves too.
      void queryClient.invalidateQueries({ queryKey: subAgentKeys.list(agencyId) });
    },
  });
}

export function useSubAgentPermissions(id: string) {
  const api = useSubAgentsApi();

  return useQuery({
    queryKey: subAgentKeys.permissions(useAgencyId(), id),
    queryFn: () => api.listPermissions(id),
  });
}

export function useChangePermission(id: string) {
  const api = useSubAgentsApi();
  const agencyId = useAgencyId();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: ({ code, deny, reason }: { code: string; deny: boolean; reason: string }) =>
      deny ? api.denyPermission(id, code, reason) : api.allowPermission(id, code),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: subAgentKeys.permissions(agencyId, id) });

      // Denying margin.view changes the "sees margins" column on the list.
      void queryClient.invalidateQueries({ queryKey: subAgentKeys.list(agencyId) });
    },
  });
}

export function useAllowance(id: string) {
  const api = useSubAgentsApi();

  return useQuery({
    queryKey: subAgentKeys.allowance(useAgencyId(), id),
    queryFn: () => api.getAllowance(id),
  });
}

export function useChangeAllowance(id: string) {
  const api = useSubAgentsApi();
  const agencyId = useAgencyId();
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (change: { limitMinor: number; period: AllowancePeriod } | { freeze: boolean }) => {
      if ('freeze' in change) {
        return change.freeze ? api.freezeAllowance(id) : api.unfreezeAllowance(id);
      }

      return api.setAllowance(id, change.limitMinor, change.period);
    },
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: subAgentKeys.allowance(agencyId, id) });
      void queryClient.invalidateQueries({ queryKey: subAgentKeys.list(agencyId) });
    },
  });
}

export function useNetworkPerformance(from: string, to: string) {
  const api = useSubAgentsApi();

  return useQuery({
    queryKey: subAgentKeys.performance(useAgencyId(), from, to),
    queryFn: () => api.networkPerformance(from, to),
  });
}
