import { createContext, useContext } from 'react';
import type { ApiClient } from '../../lib/api/client';
import type {
  EntitlementCatalogueItem,
  MigrateSubscribersRequest,
  SaveTierRequest,
  SetTierEntitlementsRequest,
  SetTierPriceRequest,
  Subscriber,
  Tier,
  TierChangeResponse,
  TierReasonRequest,
} from './types';

/**
 * The subscription tiers Trips sells, and the agencies on them.
 *
 * Every call is behind `subscription.manage`, which a Super Admin and Finance hold. Note what is
 * missing: there is no "delete a tier anybody is on". Archiving is the operation — an invoice from
 * six months ago names the tier it billed for, and a dangling name is not an answer anyone can give
 * a customer.
 */
export interface BillingApi {
  /** GET …/entitlements — the catalogue a tier picks from. Fixed in the backend's code. */
  catalogue(): Promise<EntitlementCatalogueItem[]>;

  /** GET …/tiers — every tier, with its prices, what it grants and how many agencies are on it. */
  tiers(includeArchived: boolean): Promise<Tier[]>;

  /** POST …/tiers — creates one, in draft. Nobody can subscribe until it is published. */
  create(request: SaveTierRequest): Promise<TierChangeResponse>;

  /** PUT …/tiers/{id} — renames and re-describes. The code never changes. */
  update(tierId: string, request: SaveTierRequest): Promise<TierChangeResponse>;

  /** PUT …/tiers/{id}/price — closes the old price and opens a new one. Existing subscribers keep theirs. */
  setPrice(tierId: string, request: SetTierPriceRequest): Promise<TierChangeResponse>;

  /** PUT …/tiers/{id}/entitlements — the whole truth about what the tier grants, not a patch. */
  setEntitlements(tierId: string, request: SetTierEntitlementsRequest): Promise<TierChangeResponse>;

  publish(tierId: string, request: TierReasonRequest): Promise<TierChangeResponse>;

  /** Takes the tier out of the picker and moves nobody. This is what "delete" means here. */
  archive(tierId: string, request: TierReasonRequest): Promise<TierChangeResponse>;

  restore(tierId: string, request: TierReasonRequest): Promise<TierChangeResponse>;

  /** Refused unless the tier is an unpublished draft nobody has ever been on. */
  remove(tierId: string, reason: string): Promise<TierChangeResponse>;

  /** Schedules every agency on this tier to move to another, after thirty days' notice. */
  migrate(tierId: string, request: MigrateSubscribersRequest): Promise<TierChangeResponse>;

  /** GET …/subscribers — every agency on a plan, with what it owes. */
  subscribers(tierId?: string): Promise<Subscriber[]>;
}

const BASE = '/api/v1/admin/billing';

export function createHttpBillingApi(client: ApiClient): BillingApi {
  const tier = (id: string) => `${BASE}/tiers/${encodeURIComponent(id)}`;

  return {
    catalogue: () => client.get<EntitlementCatalogueItem[]>(`${BASE}/entitlements`),
    tiers: (includeArchived) =>
      client.get<Tier[]>(`${BASE}/tiers?includeArchived=${includeArchived}`),
    create: (request) => client.post<TierChangeResponse>(`${BASE}/tiers`, request),
    update: (id, request) => client.put<TierChangeResponse>(tier(id), request),
    setPrice: (id, request) => client.put<TierChangeResponse>(`${tier(id)}/price`, request),
    setEntitlements: (id, request) =>
      client.put<TierChangeResponse>(`${tier(id)}/entitlements`, request),
    publish: (id, request) => client.post<TierChangeResponse>(`${tier(id)}/publish`, request),
    archive: (id, request) => client.post<TierChangeResponse>(`${tier(id)}/archive`, request),
    restore: (id, request) => client.post<TierChangeResponse>(`${tier(id)}/restore`, request),
    remove: (id, reason) =>
      client.del<TierChangeResponse>(`${tier(id)}?reason=${encodeURIComponent(reason)}`),
    migrate: (id, request) =>
      client.post<TierChangeResponse>(`${tier(id)}/migrate-subscribers`, request),
    subscribers: (tierId) =>
      client.get<Subscriber[]>(
        tierId ? `${BASE}/subscribers?tierId=${encodeURIComponent(tierId)}` : `${BASE}/subscribers`,
      ),
  };
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
  catalogue: () => [...billingKeys.all, 'catalogue'] as const,
  tiers: (includeArchived: boolean) => [...billingKeys.all, 'tiers', includeArchived] as const,
  subscribers: (tierId?: string) => [...billingKeys.all, 'subscribers', tierId ?? 'all'] as const,
};
