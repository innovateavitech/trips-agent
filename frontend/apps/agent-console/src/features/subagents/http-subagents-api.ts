import type { ApiClient, Schemas } from '@trips/api-client';
import { int64 } from '@trips/utils';
import { unwrap, unwrapEmpty, unwrapOptional } from '../../api/errors';
import type { SubAgentsApi } from './subagents-api';
import type {
  Allowance,
  AllowancePeriod,
  AllowanceStatus,
  NetworkMember,
  NetworkPerformance,
  SellableProductType,
  SubAgent,
  SubAgentPermission,
  SubAgentScope,
  SubAgentStatus,
} from './types';

/**
 * The sub-agent screens against the real API (issue 63).
 *
 *   listSubAgents      → GET    /api/v1/sub-agents
 *   invite             → POST   /api/v1/sub-agents
 *   freeze/unfreeze/revoke → POST /api/v1/sub-agents/{id}/{action}
 *   scopes             → GET/POST/DELETE /api/v1/sub-agents/{id}/scopes
 *   permissions        → GET/POST/DELETE /api/v1/sub-agents/{id}/permissions
 *   allowance          → GET/PUT + freeze/unfreeze on /api/v1/sub-agents/{id}/allowance
 *   networkPerformance → GET    /api/v1/network-performance
 *
 * The network performance endpoint answers with one of two shapes, and which one
 * depends on the caller's `margin.view`. `toPerformance` reads whichever arrived
 * — the margin is present or the key is simply not there — so the screens are
 * driven by the response and never by a permission check in the browser.
 */
export function createHttpSubAgentsApi({ api }: { api: ApiClient }): SubAgentsApi {
  return {
    async listSubAgents() {
      const raw = await unwrap(api.GET('/api/v1/sub-agents'));

      return {
        subAgents: raw.subAgents.map(toSubAgent),
        maxSubAgents: raw.maxSubAgents === null ? null : int64(raw.maxSubAgents),
        canAddAnother: raw.canAddAnother,
        cannotAddReason: raw.cannotAddReason ?? null,
      };
    },

    async invite(request) {
      const raw = await unwrap(
        api.POST('/api/v1/sub-agents', {
          body: {
            legalName: request.legalName,
            tradingName: request.tradingName,
            email: request.email,
          },
        }),
      );

      return {
        id: raw.id,
        email: raw.email,
        invitationToken: raw.invitationToken,
        expiresAt: raw.expiresAt,
      };
    },

    async freeze(id, reason) {
      await unwrapEmpty(
        api.POST('/api/v1/sub-agents/{subAgencyId}/freeze', {
          params: { path: { subAgencyId: id } },
          body: { reason },
        }),
      );
    },

    async unfreeze(id, reason) {
      await unwrapEmpty(
        api.POST('/api/v1/sub-agents/{subAgencyId}/unfreeze', {
          params: { path: { subAgencyId: id } },
          body: { reason },
        }),
      );
    },

    async revoke(id, reason) {
      await unwrapEmpty(
        api.POST('/api/v1/sub-agents/{subAgencyId}/revoke', {
          params: { path: { subAgencyId: id } },
          body: { reason },
        }),
      );
    },

    async listScopes(id) {
      return (
        await unwrap(
          api.GET('/api/v1/sub-agents/{subAgencyId}/scopes', {
            params: { path: { subAgencyId: id } },
          }),
        )
      ).map(toScope);
    },

    async grantScope(id, productType, supplierId) {
      await unwrapEmpty(
        api.POST('/api/v1/sub-agents/{subAgencyId}/scopes', {
          params: { path: { subAgencyId: id } },
          body: { productType, supplierId },
        }),
      );
    },

    async revokeScope(id, scopeId) {
      await unwrapEmpty(
        api.DELETE('/api/v1/sub-agents/{subAgencyId}/scopes/{scopeId}', {
          params: { path: { subAgencyId: id, scopeId } },
        }),
      );
    },

    async listPermissions(id) {
      return (
        await unwrap(
          api.GET('/api/v1/sub-agents/{subAgencyId}/permissions', {
            params: { path: { subAgencyId: id } },
          }),
        )
      ).map(toPermission);
    },

    async denyPermission(id, code, reason) {
      await unwrapEmpty(
        api.POST('/api/v1/sub-agents/{subAgencyId}/permissions/deny', {
          params: { path: { subAgencyId: id } },
          body: { permissionCode: code, reason },
        }),
      );
    },

    async allowPermission(id, code) {
      await unwrapEmpty(
        api.DELETE('/api/v1/sub-agents/{subAgencyId}/permissions/{permissionCode}', {
          params: { path: { subAgencyId: id, permissionCode: code } },
        }),
      );
    },

    async getAllowance(id) {
      // 204 when it has none, which is not the same as a limit of zero.
      const raw = await unwrapOptional(
        api.GET('/api/v1/sub-agents/{subAgencyId}/allowance', {
          params: { path: { subAgencyId: id } },
        }),
      );

      return raw === null ? null : toAllowance(raw);
    },

    async setAllowance(id, limitMinor, period) {
      return toAllowance(
        await unwrap(
          api.PUT('/api/v1/sub-agents/{subAgencyId}/allowance', {
            params: { path: { subAgencyId: id } },
            body: { limitMinor, period },
          }),
        ),
      );
    },

    async freezeAllowance(id) {
      return toAllowance(
        await unwrap(
          api.POST('/api/v1/sub-agents/{subAgencyId}/allowance/freeze', {
            params: { path: { subAgencyId: id } },
          }),
        ),
      );
    },

    async unfreezeAllowance(id) {
      return toAllowance(
        await unwrap(
          api.POST('/api/v1/sub-agents/{subAgencyId}/allowance/unfreeze', {
            params: { path: { subAgencyId: id } },
          }),
        ),
      );
    },

    async networkPerformance(from, to) {
      return toPerformance(
        await unwrap(api.GET('/api/v1/network-performance', { params: { query: { from, to } } })),
      );
    },
  };
}

type SubAgentResponse = Schemas['SubAgentResponse'];
type ScopeResponse = Schemas['SubAgentScopeResponse'];
type PermissionResponse = Schemas['SubAgentPermissionResponse'];
type AllowanceApiResponse = Schemas['AllowanceResponse'];

function toSubAgent(raw: SubAgentResponse): SubAgent {
  return {
    id: raw.id,
    legalName: raw.legalName,
    tradingName: raw.tradingName ?? null,
    slug: raw.slug,
    status: raw.status as SubAgentStatus,
    statusReason: raw.statusReason ?? null,
    createdAt: raw.createdAt,
    hasOpenInvitation: raw.hasOpenInvitation,
    scopeCount: int64(raw.scopeCount),
    deniedPermissionCount: int64(raw.deniedPermissionCount),
    canSeeMargin: raw.canSeeMargin,
    allowanceSpentMinor: optionalInt64(raw.allowanceSpentMinor),
    allowanceLimitMinor: optionalInt64(raw.allowanceLimitMinor),
    allowanceCurrency: raw.allowanceCurrency ?? null,
  };
}

function toScope(raw: ScopeResponse): SubAgentScope {
  return {
    id: raw.id,
    productType: raw.productType as SellableProductType,
    supplierId: raw.supplierId ?? null,
    supplierName: raw.supplierName ?? null,
  };
}

function toPermission(raw: PermissionResponse): SubAgentPermission {
  return {
    code: raw.code,
    category: raw.category,
    description: raw.description,
    isDenied: raw.isDenied,
    reason: raw.reason ?? null,
  };
}

function toAllowance(raw: AllowanceApiResponse): Allowance {
  return {
    subAgencyId: raw.subAgencyId,
    currency: raw.currency,
    spentMinor: int64(raw.spentMinor),
    limitMinor: int64(raw.limitMinor),
    remainingMinor: int64(raw.remainingMinor),
    period: raw.period as AllowancePeriod,
    status: raw.status as AllowanceStatus,
    resetsAt: raw.resetsAt ?? null,
  };
}

/**
 * Reads whichever of the two network shapes arrived.
 *
 * The margin-free response has no `marginMinor` property at all — not a null
 * one — so this reads it as "missing means no margin", which is exactly what
 * the server meant. Nothing here asks what the user is allowed to see.
 */
function toPerformance(raw: Record<string, unknown>): NetworkPerformance {
  const members = Array.isArray(raw['members'])
    ? (raw['members'] as Record<string, unknown>[])
    : [];

  return {
    from: String(raw['from']),
    to: String(raw['to']),
    currency: String(raw['currency']),
    orders: int64(raw['orders'] as number),
    salesMinor: int64(raw['salesMinor'] as number),
    marginMinor: 'marginMinor' in raw ? int64(raw['marginMinor'] as number) : null,
    members: members.map(toMember),
  };
}

function toMember(raw: Record<string, unknown>): NetworkMember {
  return {
    agencyId: String(raw['agencyId']),
    name: String(raw['name']),
    status: String(raw['status']) as SubAgentStatus,
    isPrincipal: raw['isPrincipal'] === true,
    orders: int64(raw['orders'] as number),
    salesMinor: int64(raw['salesMinor'] as number),
    marginMinor: 'marginMinor' in raw ? int64(raw['marginMinor'] as number) : null,
    allowanceSpentMinor: optionalInt64(raw['allowanceSpentMinor'] as number | string | null),
    allowanceLimitMinor: optionalInt64(raw['allowanceLimitMinor'] as number | string | null),
  };
}

function optionalInt64(value: number | string | null | undefined): number | null {
  return value === null || value === undefined ? null : int64(value);
}
