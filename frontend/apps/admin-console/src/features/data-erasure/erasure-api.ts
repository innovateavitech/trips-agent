import { createContext, useContext } from 'react';
import type { ApiClient } from '../../lib/api/client';
import type { ErasurePreview, ErasureRequestRecord, ErasureResult } from './types';

/**
 * What the erasure screen needs from the server.
 *
 * Each call maps to one route in TripsAgent.Api/Platform/ErasureEndpoints.cs, and all three are
 * behind `platform.erasure.execute` — including the preview, because looking a person up by email
 * across every agency is itself a read no support account should make.
 */
export interface ErasureApi {
  /** POST …/preview — who this is, and what erasing them would change. Null when nobody matches. */
  preview(agencyId: string, email: string): Promise<ErasurePreview | null>;

  /** POST … — carry it out. There is no undo and no copy. */
  erase(agencyId: string, customerId: string, reason: string): Promise<ErasureResult>;

  /** GET … — the erasures already carried out, newest first. */
  list(): Promise<ErasureRequestRecord[]>;
}

const BASE = '/api/v1/admin/erasure-requests';

export function createHttpErasureApi(client: ApiClient): ErasureApi {
  return {
    preview: async (agencyId, email) => {
      try {
        return await client.post<ErasurePreview>(`${BASE}/preview`, { agencyId, email });
      } catch (error) {
        // "Nobody by that address" is an answer the screen shows, not a failure it reports.
        if (isNotFound(error)) return null;
        throw error;
      }
    },
    erase: (agencyId, customerId, reason) =>
      client.post<ErasureResult>(BASE, { agencyId, customerId, reason }),
    list: () => client.get<ErasureRequestRecord[]>(BASE),
  };
}

function isNotFound(error: unknown): boolean {
  return typeof error === 'object' && error !== null && 'status' in error && error.status === 404;
}

const ErasureApiContext = createContext<ErasureApi | null>(null);

export const ErasureApiProvider = ErasureApiContext.Provider;

export function useErasureApi(): ErasureApi {
  const api = useContext(ErasureApiContext);
  if (!api) throw new Error('useErasureApi must be used inside an <ErasureApiProvider>.');
  return api;
}

/** Query keys, in one place, so an erasure can invalidate the list it belongs to. */
export const erasureKeys = {
  all: ['erasure-requests'] as const,
  list: () => [...erasureKeys.all, 'list'] as const,
};
