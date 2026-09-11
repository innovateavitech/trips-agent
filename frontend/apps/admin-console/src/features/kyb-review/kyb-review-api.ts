import { createContext, useContext } from 'react';
import type { ApiClient } from '../../lib/api/client';
import type { KybQueueItem, KybReviewDetail } from './types';

/**
 * Everything the KYB review screens need from the server, as one interface.
 *
 * The same ports-and-adapters shape as the agent console's `WalletApi`: screens depend on this,
 * not on `fetch`, so a test or a storybook can hand them a fake without a server running.
 *
 * Each call maps to exactly one endpoint in TripsAgent.Api/Tenancy/KybReviewEndpoints.cs, and all
 * of them require the `kyb.review` permission server-side.
 */
export interface KybReviewApi {
  /** GET /api/v1/admin/kyb/submissions — awaiting a decision, oldest first. */
  getQueue(): Promise<KybQueueItem[]>;

  /** GET /api/v1/admin/kyb/submissions/{id} — with a fresh signed link per document. */
  getSubmission(submissionId: string): Promise<KybReviewDetail>;

  /** POST …/approve. 409 if it is no longer awaiting a decision. */
  approve(submissionId: string): Promise<void>;

  /** POST …/reject. 400 without a reason; the reason is shown to the agency word for word. */
  reject(submissionId: string, reason: string): Promise<void>;
}

const BASE = '/api/v1/admin/kyb/submissions';

export function createHttpKybReviewApi(client: ApiClient): KybReviewApi {
  // encodeURIComponent even though ids are GUIDs: the id comes from the address bar, and an
  // edited one must not be able to reach a different path.
  const submission = (id: string) => `${BASE}/${encodeURIComponent(id)}`;

  return {
    getQueue: () => client.get<KybQueueItem[]>(BASE),
    getSubmission: (id) => client.get<KybReviewDetail>(submission(id)),
    approve: (id) => client.post(`${submission(id)}/approve`),
    reject: (id, reason) => client.post(`${submission(id)}/reject`, { reason }),
  };
}

const KybReviewApiContext = createContext<KybReviewApi | null>(null);

export const KybReviewApiProvider = KybReviewApiContext.Provider;

export function useKybReviewApi(): KybReviewApi {
  const api = useContext(KybReviewApiContext);
  if (!api) throw new Error('useKybReviewApi must be used inside a <KybReviewApiProvider>.');
  return api;
}

/**
 * Query keys, in one place. Everything starts with `['kyb-review']`, so a decision can invalidate
 * the queue and every open submission in one call.
 */
export const kybReviewKeys = {
  all: ['kyb-review'] as const,
  queue: () => [...kybReviewKeys.all, 'queue'] as const,
  submission: (submissionId: string) => [...kybReviewKeys.all, 'submission', submissionId] as const,
};
