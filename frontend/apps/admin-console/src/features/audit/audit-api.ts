import { createContext, useContext } from 'react';
import type { ApiClient } from '../../lib/api/client';
import type { AuditLogPage, AuditLogQuery } from './types';

/**
 * What the audit viewer needs from the server.
 *
 * Read-only, and that is the whole interface: there is no write route because there is no write
 * path — the table refuses updates and deletes outright, which is what makes it worth reading.
 */
export interface AuditApi {
  /** GET /api/v1/admin/audit-logs — one page of the trail, newest first. */
  search(query: AuditLogQuery): Promise<AuditLogPage>;

  /**
   * GET /api/v1/admin/audit-logs/actions — the action names actually present in the trail.
   *
   * Read from the data rather than from a list in the console, so a new action starts appearing
   * in the filter the day it is first written instead of the day somebody remembers to add it.
   */
  actions(): Promise<string[]>;
}

const BASE = '/api/v1/admin/audit-logs';

export function createHttpAuditApi(client: ApiClient): AuditApi {
  return {
    search: (query) => client.get<AuditLogPage>(`${BASE}${toQueryString(query)}`),
    actions: () => client.get<string[]>(`${BASE}/actions`),
  };
}

/** Turns the query object into a query string, leaving out everything unset. */
export function toQueryString(query: AuditLogQuery): string {
  const params = new URLSearchParams();

  if (query.agencyId) params.set('agencyId', query.agencyId);
  if (query.actorUserId) params.set('actorUserId', query.actorUserId);
  if (query.action) params.set('action', query.action);
  if (query.entityType) params.set('entityType', query.entityType);
  if (query.entityId) params.set('entityId', query.entityId);
  if (query.from) params.set('from', query.from);
  if (query.to) params.set('to', query.to);
  if (query.page && query.page > 1) params.set('page', String(query.page));
  if (query.pageSize) params.set('pageSize', String(query.pageSize));

  const encoded = params.toString();
  return encoded ? `?${encoded}` : '';
}

const AuditApiContext = createContext<AuditApi | null>(null);

export const AuditApiProvider = AuditApiContext.Provider;

export function useAuditApi(): AuditApi {
  const api = useContext(AuditApiContext);
  if (!api) throw new Error('useAuditApi must be used inside an <AuditApiProvider>.');
  return api;
}

export const auditKeys = {
  all: ['audit'] as const,
  page: (query: AuditLogQuery) => [...auditKeys.all, 'page', toQueryString(query)] as const,
  actions: () => [...auditKeys.all, 'actions'] as const,
};
