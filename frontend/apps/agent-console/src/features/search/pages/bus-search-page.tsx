import { Bus } from 'lucide-react';
import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { Card, EmptyState } from '@trips/ui';
import { PageHeader } from '../../../shell/page-header';
import { BusResults } from '../components/bus-results';
import { BusSearchForm } from '../components/bus-search-form';
import { useBusSearch, type SearchRequest } from '../search-api';
import { addDays, todayIn } from '../search-rules';
import type { BusSearchCriteria } from '../types';

/** Lagos (Jibowu) to Abuja (Utako) in three days' time — the busiest road route there is. */
function defaultCriteria(): BusSearchCriteria {
  return {
    tripType: 'one_way',
    departureTerminalId: 'trm_lag_jibowu',
    arrivalTerminalId: 'trm_abj_utako',
    date: addDays(todayIn(), 3),
    returnDate: null,
    passengers: 1,
  };
}

/** FRD §2.3 — intercity bus search, the same flow as flights with terminals instead of airports. */
export function BusSearchPage() {
  const [initial] = useState(defaultCriteria);
  const [request, setRequest] = useState<SearchRequest<BusSearchCriteria> | null>(null);
  const query = useBusSearch(request);
  const navigate = useNavigate();

  function search(criteria: BusSearchCriteria) {
    setRequest((current) => ({ criteria, run: (current?.run ?? 0) + 1 }));
  }

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title="Buses"
        description="Intercity coaches across Nigeria. The price on each departure is what your customer pays."
      />

      <Card className="p-5">
        <BusSearchForm initial={initial} onSearch={search} searching={request !== null && query.isFetching} />
      </Card>

      {request === null ? (
        <EmptyState size="page" headingLevel={2} icon={<Bus className="h-5 w-5" />} title="Where is your customer going by road?">
          Search GIG Mobility, ABC Transport, Chisco and more at once, with the seats each bus has
          left.
        </EmptyState>
      ) : (
        <BusResults
          query={query}
          passengers={request.criteria.passengers}
          onResearch={() => search(request.criteria)}
          // The booking flow (#53) takes it from here, including seat selection.
          onSelect={(offer, direction) =>
            navigate('/book/bus', { state: { product: 'bus', offerId: offer.id, direction } })
          }
        />
      )}
    </div>
  );
}
