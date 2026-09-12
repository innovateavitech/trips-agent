import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createContext, useContext } from 'react';
import { useCurrentUser } from '../../auth/auth-provider';
import type {
  Departure,
  DepartureAction,
  DepartureRequest,
  ManifestEntry,
  WaitlistEntry,
} from './types';

/**
 * What the departure screens need from the server, as one port. The stand-in
 * in `mock/` sits behind it until the departures API (build plan F6) lands;
 * then an HTTP adapter replaces it in `main.tsx` and no screen changes.
 */
export interface DeparturesApi {
  listDepartures(filter: { productId?: string }): Promise<Departure[]>;
  getDeparture(id: string): Promise<Departure>;
  createDeparture(productId: string, request: DepartureRequest): Promise<Departure>;
  /** Refused when `version` is stale, or when the capacity would drop below seats already taken. */
  saveDeparture(id: string, request: DepartureRequest, version: number): Promise<Departure>;
  act(id: string, action: DepartureAction): Promise<Departure>;
  getManifest(id: string): Promise<ManifestEntry[]>;
  getWaitlist(id: string): Promise<WaitlistEntry[]>;
}

const DeparturesApiContext = createContext<DeparturesApi | null>(null);

export const DeparturesApiProvider = DeparturesApiContext.Provider;

function useDeparturesApi(): DeparturesApi {
  const api = useContext(DeparturesApiContext);
  if (!api) throw new Error('useDeparturesApi must be used inside a <DeparturesApiProvider>.');
  return api;
}

function useAgencyId(): string {
  return useCurrentUser().agency?.id ?? 'none';
}

/** Keyed by agency, so switching agency never shows the last one's departures. */
export const departureKeys = {
  all: (agencyId: string) => ['departures', agencyId] as const,
  list: (agencyId: string, productId: string | undefined) =>
    [...departureKeys.all(agencyId), 'list', productId ?? 'all'] as const,
  detail: (agencyId: string, id: string) => [...departureKeys.all(agencyId), 'detail', id] as const,
  manifest: (agencyId: string, id: string) =>
    [...departureKeys.all(agencyId), 'manifest', id] as const,
  waitlist: (agencyId: string, id: string) =>
    [...departureKeys.all(agencyId), 'waitlist', id] as const,
};

export function useDepartures(productId?: string) {
  const api = useDeparturesApi();
  const agencyId = useAgencyId();

  return useQuery({
    queryKey: departureKeys.list(agencyId, productId),
    queryFn: () => api.listDepartures(productId ? { productId } : {}),
    // Seats sell while the list is open; a minute keeps the counts honest.
    refetchInterval: 60_000,
  });
}

export function useDeparture(id: string) {
  const api = useDeparturesApi();
  const agencyId = useAgencyId();

  return useQuery({
    queryKey: departureKeys.detail(agencyId, id),
    queryFn: () => api.getDeparture(id),
  });
}

export function useManifest(id: string) {
  const api = useDeparturesApi();
  const agencyId = useAgencyId();

  return useQuery({
    queryKey: departureKeys.manifest(agencyId, id),
    queryFn: () => api.getManifest(id),
  });
}

export function useWaitlist(id: string) {
  const api = useDeparturesApi();
  const agencyId = useAgencyId();

  return useQuery({
    queryKey: departureKeys.waitlist(agencyId, id),
    queryFn: () => api.getWaitlist(id),
  });
}

/** After any change: the departure's own entry is the answer, and every list is out of date. */
function useDepartureChanged() {
  const queryClient = useQueryClient();
  const agencyId = useAgencyId();

  return (departure: Departure) => {
    queryClient.setQueryData(departureKeys.detail(agencyId, departure.id), departure);
    void queryClient.invalidateQueries({ queryKey: [...departureKeys.all(agencyId), 'list'] });
  };
}

/** Creates a departure the first time, and saves it after that. */
export function useSaveDeparture() {
  const api = useDeparturesApi();
  const changed = useDepartureChanged();

  return useMutation({
    mutationFn: (input: {
      productId: string;
      existing: Pick<Departure, 'id' | 'version'> | null;
      request: DepartureRequest;
    }) =>
      input.existing
        ? api.saveDeparture(input.existing.id, input.request, input.existing.version)
        : api.createDeparture(input.productId, input.request),
    onSuccess: changed,
  });
}

export function useDepartureAction(id: string) {
  const api = useDeparturesApi();
  const changed = useDepartureChanged();

  return useMutation({
    mutationFn: (action: DepartureAction) => api.act(id, action),
    onSuccess: changed,
  });
}
