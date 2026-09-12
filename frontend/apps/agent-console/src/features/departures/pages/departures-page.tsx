import { CalendarDays } from 'lucide-react';
import { useMemo, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import {
  Card,
  EmptyState,
  ErrorState,
  LoadingState,
  SegmentedControl,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
  buttonVariants,
} from '@trips/ui';
import { formatMoneyShort } from '@trips/utils';
import { describeError } from '../../../api/errors';
import { useCurrentUser } from '../../../auth/auth-provider';
import { PageHeader } from '../../../shell/page-header';
import { canEditCatalog } from '../../catalog/catalog-rules';
import {
  STATUS_GROUPS,
  describeSeats,
  formatDay,
  groupCounts,
  inGroup,
  lowestPriceMinor,
  type StatusGroup,
} from '../departure-rules';
import { useDepartures } from '../departures-api';
import { DepartureStatusBadge, SeatsBar } from '../components/departure-parts';

/**
 * Build plan F6 — every dated departure, soonest first: how full each one is,
 * whether it will run, and who is waiting. `?product=` narrows it to one tour.
 */
export function DeparturesPage() {
  const [params] = useSearchParams();
  const productId = params.get('product') ?? undefined;
  const user = useCurrentUser();
  const departures = useDepartures(productId);
  const [group, setGroup] = useState<StatusGroup>('all');

  const all = useMemo(() => departures.data ?? [], [departures.data]);
  const counts = useMemo(() => groupCounts(all), [all]);
  const visible = all.filter((departure) => inGroup(departure.status, group));
  const productTitle = productId ? all[0]?.productTitle : undefined;
  const newPath = productId ? `/departures/new?product=${productId}` : '/departures/new';

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title={productTitle ? `Departures · ${productTitle}` : 'Group departures'}
        description="Dated runs of your tours and packages, sold by the seat."
        actions={
          canEditCatalog(user.roles) ? (
            <Link to={newPath} className={buttonVariants({ size: 'sm' })}>
              New departure
            </Link>
          ) : null
        }
      />

      <SegmentedControl
        label="Filter by status"
        options={STATUS_GROUPS.map((option) =>
          departures.data ? { ...option, count: counts[option.value] } : option,
        )}
        value={group}
        onChange={setGroup}
      />

      {departures.isPending ? <LoadingState label="Loading departures" /> : null}
      {departures.isError ? (
        <ErrorState
          {...describeError(departures.error)}
          onRetry={() => void departures.refetch()}
          retrying={departures.isFetching}
        />
      ) : null}

      {departures.data && visible.length === 0 ? (
        <EmptyState
          icon={<CalendarDays aria-hidden="true" className="h-5 w-5" />}
          title={all.length === 0 ? 'No departures yet' : 'None in this group'}
        >
          {all.length === 0
            ? 'Add a dated departure to a tour or package to sell it by the seat.'
            : 'Try another status.'}
        </EmptyState>
      ) : null}

      {visible.length > 0 ? (
        <Card className="overflow-hidden p-0">
          <div className="overflow-x-auto">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Departs</TableHead>
                  <TableHead>Status</TableHead>
                  <TableHead className="w-56">Seats</TableHead>
                  <TableHead className="text-right">From</TableHead>
                  <TableHead>Waiting</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {visible.map((departure) => {
                  const from = lowestPriceMinor(departure.priceTiers);

                  return (
                    <TableRow key={departure.id}>
                      <TableCell>
                        <Link
                          to={`/departures/${departure.id}`}
                          className="font-medium text-foreground underline-offset-4 hover:text-primary hover:underline"
                        >
                          {formatDay(departure.departureDate)}
                        </Link>
                        <p className="text-xs text-muted-foreground">{departure.productTitle}</p>
                      </TableCell>
                      <TableCell>
                        <DepartureStatusBadge status={departure.status} />
                      </TableCell>
                      <TableCell>
                        <div className="flex flex-col gap-1">
                          <SeatsBar departure={departure} />
                          <span className="text-xs text-muted-foreground">
                            {describeSeats(departure)}
                          </span>
                        </div>
                      </TableCell>
                      <TableCell className="text-right tabular-nums">
                        {from === null ? '—' : formatMoneyShort(from, departure.currency)}
                      </TableCell>
                      <TableCell className="text-muted-foreground">
                        {departure.waitlistCount > 0
                          ? `${departure.waitlistCount} on the waitlist`
                          : '—'}
                      </TableCell>
                    </TableRow>
                  );
                })}
              </TableBody>
            </Table>
          </div>
        </Card>
      ) : null}
    </div>
  );
}
