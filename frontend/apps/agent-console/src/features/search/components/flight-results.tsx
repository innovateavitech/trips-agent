import { SlidersHorizontal } from 'lucide-react';
import { useMemo, useState } from 'react';
import { Button, Card, EmptyState, SegmentedControl } from '@trips/ui';
import { cx } from '../class-names';
import type { SearchView } from '../search-api';
import {
  activeFilterCount,
  applyFlightFilters,
  describePassengers,
  hasActiveFilters,
  NO_FLIGHT_FILTERS,
  sortFlights,
  type FlightSort,
} from '../search-rules';
import type { FlightOffer, Passengers, SearchResult } from '../types';
import { useCountdown } from '../use-countdown';
import { FlightFilterPanel } from './flight-filter-panel';
import { FlightOfferCard } from './flight-offer-card';
import { FareExpiry, ResultsSkeleton, SearchFailure } from './result-states';

const SORTS = [
  { value: 'cheapest', label: 'Cheapest' },
  { value: 'fastest', label: 'Fastest' },
  { value: 'earliest', label: 'Earliest' },
  { value: 'latest', label: 'Latest' },
] as const;

export interface FlightResultsProps {
  query: SearchView<SearchResult<FlightOffer>>;
  passengers: Passengers;
  onResearch: () => void;
  onSelect: (offer: FlightOffer) => void;
}

/**
 * Loading, failed, nothing flies, or fares — decided here, in that order, so
 * a failure can never fall through to an empty list.
 */
export function FlightResults({ query, passengers, onResearch, onSelect }: FlightResultsProps) {
  if (query.isPending) return <ResultsSkeleton label="Searching the airlines for live fares" />;

  if (query.isError) {
    return (
      <SearchFailure
        error={query.error}
        supplier="airline"
        onRetry={() => void query.refetch()}
        retrying={query.isFetching}
      />
    );
  }

  if (!query.data) return null;

  if (query.data.offers.length === 0) {
    return (
      <EmptyState
        title="No flights on that route and date"
        action={
          <Button variant="outline" size="sm" onClick={onResearch}>
            Search again
          </Button>
        }
      >
        The airlines have nothing to sell for this search. A day either side often has seats, and so
        does a nearby airport.
      </EmptyState>
    );
  }

  // Keyed on the search, so a new search starts with fresh filters rather than
  // silently hiding results with the last search's "Direct only".
  return (
    <FlightResultList
      key={query.data.searchedAt}
      result={query.data}
      passengers={passengers}
      onResearch={onResearch}
      onSelect={onSelect}
    />
  );
}

function FlightResultList({
  result,
  passengers,
  onResearch,
  onSelect,
}: {
  result: SearchResult<FlightOffer>;
  passengers: Passengers;
  onResearch: () => void;
  onSelect: (offer: FlightOffer) => void;
}) {
  const [filters, setFilters] = useState(NO_FLIGHT_FILTERS);
  const [sort, setSort] = useState<FlightSort>('cheapest');
  const [showFilters, setShowFilters] = useState(false);
  const secondsLeft = useCountdown(result.expiresAt);
  const expired = secondsLeft === 0;
  const activeFilters = activeFilterCount(filters);
  const party = describePassengers(passengers);

  const visible = useMemo(
    () => sortFlights(applyFlightFilters(result.offers, filters), sort),
    [result.offers, filters, sort],
  );

  return (
    <section aria-labelledby="flight-results-heading" className="flex flex-col gap-4">
      <FareExpiry secondsLeft={secondsLeft} onResearch={onResearch} />

      <div className="grid items-start gap-6 lg:grid-cols-12">
        <aside className="lg:col-span-3" aria-label="Filter fares">
          <Button
            variant="outline"
            fullWidth
            className="lg:hidden"
            aria-expanded={showFilters}
            aria-controls="flight-filters"
            onClick={() => setShowFilters((open) => !open)}
          >
            <SlidersHorizontal className="h-4 w-4" aria-hidden="true" />
            Filters{activeFilters > 0 ? ` (${activeFilters})` : ''}
          </Button>
          <Card
            id="flight-filters"
            className={cx('mt-3 p-4 lg:mt-0', showFilters ? 'block' : 'hidden lg:block')}
          >
            <div className="mb-4 flex items-center justify-between">
              <h2 className="text-sm font-semibold text-foreground">Filters</h2>
              <Button
                variant="link"
                size="sm"
                className="px-0"
                disabled={!hasActiveFilters(filters)}
                onClick={() => setFilters(NO_FLIGHT_FILTERS)}
              >
                Clear all
              </Button>
            </div>
            <FlightFilterPanel offers={result.offers} filters={filters} onChange={setFilters} />
          </Card>
        </aside>

        <div className="flex flex-col gap-3 lg:col-span-9">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <h2 id="flight-results-heading" className="text-sm text-muted-foreground">
              <span className="font-semibold text-foreground">{visible.length}</span>
              {visible.length === result.offers.length ? '' : ` of ${result.offers.length}`}{' '}
              {result.offers.length === 1 ? 'fare' : 'fares'} · {party}
            </h2>
            <SegmentedControl label="Sort fares" options={SORTS} value={sort} onChange={setSort} />
          </div>

          {visible.length === 0 ? (
            <Card>
              <EmptyState
                title="No fares match these filters"
                action={
                  <Button variant="outline" size="sm" onClick={() => setFilters(NO_FLIGHT_FILTERS)}>
                    Clear filters
                  </Button>
                }
              >
                {result.offers.length} fares were found; the filters are hiding all of them.
              </EmptyState>
            </Card>
          ) : (
            <ul className="flex flex-col gap-3">
              {visible.map((offer) => (
                <li key={offer.id}>
                  <FlightOfferCard
                    offer={offer}
                    priceCaption={`Total for ${party}`}
                    expired={expired}
                    onSelect={onSelect}
                  />
                </li>
              ))}
            </ul>
          )}
        </div>
      </div>
    </section>
  );
}
