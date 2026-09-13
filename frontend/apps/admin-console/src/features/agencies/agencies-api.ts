import { createContext, useContext } from 'react';
import type { ApiClient } from '../../lib/api/client';
import type {
  AgencyDirectory,
  AgencyDirectoryQuery,
  AgencyProfile,
  AgencyStatusChange,
  UpdateAgencyRequest,
} from './types';

/**
 * Everything the agency screens need from the server, as one interface.
 *
 * The same ports-and-adapters shape as `KybReviewApi`: screens depend on this, not on `fetch`, so
 * a test can hand them a fake without a server running.
 *
 * Each call maps to exactly one route in TripsAgent.Api/Platform/AgencyAdminEndpoints.cs, and each
 * of those requires its own permission — reading is `agency.view`, editing `agency.manage`,
 * suspending `agency.suspend`, and terminating and exporting their own codes again.
 */
export interface AgenciesApi {
  /** GET /api/v1/admin/agencies — one page of the directory. */
  list(query: AgencyDirectoryQuery): Promise<AgencyDirectory>;

  /** GET /api/v1/admin/agencies/{id} — the profile, with staff, wallet and sub-agents. */
  get(agencyId: string): Promise<AgencyProfile>;

  /** PUT /api/v1/admin/agencies/{id} — an edit, which always carries a reason. */
  update(agencyId: string, request: UpdateAgencyRequest): Promise<AgencyStatusChange>;

  /** POST …/verify, …/suspend, …/reinstate or …/terminate. 400 without a usable reason. */
  act(agencyId: string, action: string, reason: string): Promise<AgencyStatusChange>;

  /**
   * GET …/export — the whole agency as JSON, ready to be saved.
   *
   * Returns the text rather than triggering the download itself, so the screen decides what to do
   * with it and a test never has to fake a browser's download machinery.
   */
  exportData(agencyId: string): Promise<{ fileName: string; json: string }>;
}

const BASE = '/api/v1/admin/agencies';

export function createHttpAgenciesApi(client: ApiClient): AgenciesApi {
  // encodeURIComponent even though ids are GUIDs: the id comes from the address bar, and an
  // edited one must not be able to reach a different path.
  const agency = (id: string) => `${BASE}/${encodeURIComponent(id)}`;

  return {
    list: (query) => client.get<AgencyDirectory>(`${BASE}${toQueryString(query)}`),
    get: (id) => client.get<AgencyProfile>(agency(id)),
    update: (id, request) => client.put<AgencyStatusChange>(agency(id), request),
    act: (id, action, reason) =>
      client.post<AgencyStatusChange>(`${agency(id)}/${encodeURIComponent(action)}`, { reason }),
    exportData: (id) => client.download(`${agency(id)}/export`),
  };
}

/** Turns the query object into a query string, leaving out everything unset. */
export function toQueryString(query: AgencyDirectoryQuery): string {
  const params = new URLSearchParams();

  if (query.search?.trim()) params.set('search', query.search.trim());
  if (query.status) params.set('status', query.status);
  if (query.type) params.set('type', query.type);
  if (query.sort && query.sort !== 'Newest') params.set('sort', query.sort);
  if (query.page && query.page > 1) params.set('page', String(query.page));
  if (query.pageSize) params.set('pageSize', String(query.pageSize));

  const encoded = params.toString();
  return encoded ? `?${encoded}` : '';
}

const AgenciesApiContext = createContext<AgenciesApi | null>(null);

export const AgenciesApiProvider = AgenciesApiContext.Provider;

export function useAgenciesApi(): AgenciesApi {
  const api = useContext(AgenciesApiContext);
  if (!api) throw new Error('useAgenciesApi must be used inside an <AgenciesApiProvider>.');
  return api;
}

/**
 * Query keys, in one place. Everything starts with `['agencies']`, so any action can invalidate
 * the directory and every open profile in one call.
 */
export const agencyKeys = {
  all: ['agencies'] as const,
  list: (query: AgencyDirectoryQuery) => [...agencyKeys.all, 'list', toQueryString(query)] as const,
  profile: (agencyId: string) => [...agencyKeys.all, 'profile', agencyId] as const,
};
