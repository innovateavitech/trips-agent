import { useMemo, useState } from 'react';
import { Button, Card, EmptyState, SegmentedControl, Select } from '@trips/ui';
import type { SearchView } from '../search-api';
import { describeBusPassengers, operatorCounts, sortBuses, type BusSort } from '../search-rules';
import type { BusOffer, BusSearchResult } from '../types';
import { useCountdown } from '../use-countdown';
import { BusOfferCard } from './bus-offer-card';
import { FareExpiry, ResultsSkeleton, SearchFailure } from './result-states';

const SORTS = [
  { value: 'earliest', label: 'Earliest' },
  { value: 'cheapest', label: 'Cheapest' },
  { value: 'fastest', label: 'Fastest' },
] as const;

type Direction = 'outbound' | 'return';

export interface BusResultsProps {
  query: SearchView<BusSearchResult>;
  passengers: number;
  onResearch: () => void;
  onSelect: (offer: BusOffer, direction: Direction) => void;
}

export function BusResults({ query, passengers, onResearch, onSelect }: BusResultsProps) {
  if (query.isPending) return <ResultsSkeleton label="Checking seats with the bus operators" />;

  if (query.isError) {
    return (
      <SearchFailure
        error={query.error}
        supplier="bus operator"
        onRetry={() => void query.refetch()}
        retrying={query.isFetching}
      />
    );
  }

  if (!query.data) return null;

  return (
    <BusResultList
      key={query.data.searchedAt}
      result={query.data}
      passengers={passengers}
      onResearch={onResearch}
      onSelect={onSelect}
    />
  );
}

function BusResultList({
  result,
  passengers,
  onResearch,
  onSelect,
}: {
  result: BusSearchResult;
  passengers: number;
  onResearch: () => void;
  onSelect: (offer: BusOffer, direction: Direction) => void;
}) {
  const [direction, setDirection] = useState<Direction>('outbound');
  const [sort, setSort] = useState<BusSort>('earliest');
  const [operator, setOperator] = useState('');
  const secondsLeft = useCountdown(result.expiresAt);
  const expired = secondsLeft === 0;
  const party = describeBusPassengers(passengers);

  const offers =
    direction === 'return' && result.returnOffers ? result.returnOffers : result.offers;
  const operators = operatorCounts(offers);
  const visible = useMemo(
    () =>
      sortBuses(operator ? offers.filter((offer) => offer.operator === operator) : offers, sort),
    [offers, operator, sort],
  );

  return (
    <section aria-labelledby="bus-results-heading" className="flex flex-col gap-4">
      <FareExpiry secondsLeft={secondsLeft} onResearch={onResearch} />

      {result.returnOffers ? (
        <SegmentedControl
          label="Journey"
          className="self-start"
          options={[
            { value: 'outbound', label: 'Outbound', count: result.offers.length },
            { value: 'return', label: 'Return', count: result.returnOffers.length },
          ]}
          value={direction}
          onChange={(next) => {
            setDirection(next);
            // The return may be run by different operators; a leftover filter would hide it all.
            setOperator('');
          }}
        />
      ) : null}

      <div className="flex flex-wrap items-end justify-between gap-3">
        <h2 id="bus-results-heading" className="text-sm text-muted-foreground">
          <span className="font-semibold text-foreground">{visible.length}</span>{' '}
          {visible.length === 1 ? 'departure' : 'departures'} · {party}
        </h2>
        <div className="flex flex-wrap items-end gap-3">
          <div className="w-full sm:w-56">
            <Select
              label="Operator"
              labelHidden
              value={operator}
              onChange={(event) => setOperator(event.target.value)}
            >
              <option value="">All operators</option>
              {operators.map((entry) => (
                <option key={entry.name} value={entry.name}>
                  {entry.name} ({entry.count})
                </option>
              ))}
            </Select>
          </div>
          <SegmentedControl
            label="Sort departures"
            options={SORTS}
            value={sort}
            onChange={setSort}
          />
        </div>
      </div>

      {visible.length === 0 ? (
        <Card>
          <EmptyState
            title="No buses on that route and date"
            action={
              <Button variant="outline" size="sm" onClick={onResearch}>
                Search again
              </Button>
            }
          >
            The operators have nothing running for this search. Another terminal in the same city
            often does.
          </EmptyState>
        </Card>
      ) : (
        <ul className="flex flex-col gap-3">
          {visible.map((offer) => (
            <li key={offer.id}>
              <BusOfferCard
                offer={offer}
                passengers={passengers}
                priceCaption={`Total for ${party}`}
                expired={expired}
                onSelect={(chosen) => onSelect(chosen, direction)}
              />
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
