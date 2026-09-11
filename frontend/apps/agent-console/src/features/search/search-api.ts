import { useQuery } from '@tanstack/react-query';
import { createContext, useContext } from 'react';
import { useCurrentUser } from '../../auth/auth-provider';
import { canViewMargin, redactBusResult, redactFlightResult } from './search-rules';
import type {
  BusSearchCriteria,
  BusSearchResult,
  FlightOffer,
  FlightSearchCriteria,
  SearchResult,
} from './types';

/**
 * Everything the search screens need from the server, as one interface — the
 * same port-and-adapter shape as `WalletApi`.
 *
 * Today `main.tsx` puts the mock in `mock/` behind it, because the supplier
 * search endpoints (#33, #34) are not built yet. When they are, an HTTP adapter
 * over the generated client replaces the mock and no component changes.
 */
export interface SearchApi {
  searchFlights(
    criteria: FlightSearchCriteria,
    signal?: AbortSignal,
  ): Promise<SearchResult<FlightOffer>>;

  searchBuses(criteria: BusSearchCriteria, signal?: AbortSignal): Promise<BusSearchResult>;
}

const SearchApiContext = createContext<SearchApi | null>(null);

export const SearchApiProvider = SearchApiContext.Provider;

function useSearchApi(): SearchApi {
  const api = useContext(SearchApiContext);
  if (!api) throw new Error('useSearchApi must be used inside a <SearchApiProvider>.');
  return api;
}

/**
 * A search, numbered.
 *
 * The number is part of the query key, so pressing Search again — or "Search
 * again" on expired fares — always asks the supplier afresh. Without it, the
 * same criteria would replay the cached answer: a price the airline may no
 * longer hold.
 */
export interface SearchRequest<TCriteria> {
  criteria: TCriteria;
  run: number;
}

/** The parts of a query the result lists read — narrow on purpose, so a test can hand one in. */
export interface SearchView<TResult> {
  isPending: boolean;
  isError: boolean;
  isFetching: boolean;
  data: TResult | undefined;
  error: unknown;
  refetch: () => unknown;
}

/**
 * Keys carry the agency, so one agency's cached fares can never be shown to
 * the next — on top of the cache clear every sign-in and agency switch does.
 */
export const searchKeys = {
  all: ['search'] as const,
  flights: (agencyId: string, request: SearchRequest<FlightSearchCriteria> | null) =>
    [...searchKeys.all, 'flights', agencyId, request] as const,
  buses: (agencyId: string, request: SearchRequest<BusSearchCriteria> | null) =>
    [...searchKeys.all, 'buses', agencyId, request] as const,
};

const SEARCH_QUERY_OPTIONS = {
  // The API already retries the supplier behind a circuit breaker (#33). Retrying
  // here as well would stretch one 20-second timeout into a minute of skeleton.
  retry: false,
  // A fare list is a snapshot with an expiry, not something to refresh behind the
  // agent's back — the price would move while they were reading it.
  staleTime: Infinity,
  refetchOnWindowFocus: false,
} as const;

export function useFlightSearch(request: SearchRequest<FlightSearchCriteria> | null) {
  const api = useSearchApi();
  const user = useCurrentUser();
  const canView = canViewMargin(user.roles);

  return useQuery({
    queryKey: searchKeys.flights(user.agency?.id ?? 'none', request),
    queryFn: ({ signal }) => {
      if (!request) throw new Error('There is no search to run.');
      return api.searchFlights(request.criteria, signal);
    },
    enabled: request !== null,
    ...SEARCH_QUERY_OPTIONS,
    // Stripped before any component sees it: the API leaves the margin out for
    // someone without margin.view, and this is the second lock on the same door.
    select: (result) => redactFlightResult(result, canView),
  });
}

export function useBusSearch(request: SearchRequest<BusSearchCriteria> | null) {
  const api = useSearchApi();
  const user = useCurrentUser();
  const canView = canViewMargin(user.roles);

  return useQuery({
    queryKey: searchKeys.buses(user.agency?.id ?? 'none', request),
    queryFn: ({ signal }) => {
      if (!request) throw new Error('There is no search to run.');
      return api.searchBuses(request.criteria, signal);
    },
    enabled: request !== null,
    ...SEARCH_QUERY_OPTIONS,
    select: (result) => redactBusResult(result, canView),
  });
}
