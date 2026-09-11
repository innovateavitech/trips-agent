import { Plane } from 'lucide-react';
import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { Card, EmptyState } from '@trips/ui';
import { PageHeader } from '../../../shell/page-header';
import { FlightResults } from '../components/flight-results';
import { FlightSearchForm } from '../components/flight-search-form';
import { useFlightSearch, type SearchRequest } from '../search-api';
import { addDays, todayIn } from '../search-rules';
import type { FlightSearchCriteria } from '../types';

/** Lagos to Abuja next week, back three days later — the search most agents run first. */
function defaultCriteria(): FlightSearchCriteria {
  const depart = addDays(todayIn(), 7);
  return {
    tripType: 'round_trip',
    legs: [
      { origin: 'LOS', destination: 'ABV', date: depart },
      { origin: 'ABV', destination: 'LOS', date: addDays(depart, 3) },
    ],
    passengers: { adults: 1, children: 0, infants: 0 },
    cabin: 'economy',
  };
}

/**
 * FRD §2.3 — the flight search. The core earning screen: an agent with a
 * customer on the phone searches, compares, and picks a fare to book.
 */
export function FlightSearchPage() {
  const [initial] = useState(defaultCriteria);
  const [request, setRequest] = useState<SearchRequest<FlightSearchCriteria> | null>(null);
  const query = useFlightSearch(request);
  const navigate = useNavigate();

  function search(criteria: FlightSearchCriteria) {
    setRequest((current) => ({ criteria, run: (current?.run ?? 0) + 1 }));
  }

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title="Flights"
        description="Live domestic and international fares. The price on each fare is what your customer pays."
      />

      <Card className="p-5">
        <FlightSearchForm initial={initial} onSearch={search} searching={request !== null && query.isFetching} />
      </Card>

      {request === null ? (
        <EmptyState size="page" headingLevel={2} icon={<Plane className="h-5 w-5" />} title="Where is your customer flying?">
          Search Air Peace, Ibom Air, Arik and the international carriers at once. Fares are live,
          and held for ten minutes once you have them.
        </EmptyState>
      ) : (
        <FlightResults
          query={query}
          passengers={request.criteria.passengers}
          onResearch={() => search(request.criteria)}
          // The booking flow (#53) takes it from here.
          onSelect={(offer) => navigate('/book/flight', { state: { product: 'flight', offerId: offer.id } })}
        />
      )}
    </div>
  );
}
