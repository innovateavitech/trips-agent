import { Bus, Plane, Ticket } from 'lucide-react';
import { useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import {
  Badge,
  Button,
  Card,
  EmptyState,
  ErrorState,
  Input,
  SegmentedControl,
  Select,
  Skeleton,
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
import { PageHeader } from '../../../shell/page-header';
import { STATUS_DISPLAY, formatDeparture } from '../../dashboard/booking-display';
import { useBookings } from '../bookings-api';
import { STATUS_FILTERS, filterBookings, statusCounts } from '../bookings-rules';
import {
  NO_BOOKING_FILTERS,
  type BookingFilters,
  type BookingListItem,
  type ProductKind,
} from '../types';

/**
 * #54 — every booking the agency has made, and where each one stands. Status
 * badges come from the dashboard's own table, so "Awaiting ticket" means the
 * same thing everywhere and never implies a ticket that does not exist yet.
 */
export function BookingsPage() {
  const bookings = useBookings();
  const [filters, setFilters] = useState<BookingFilters>(NO_BOOKING_FILTERS);

  const all = useMemo(() => bookings.data ?? [], [bookings.data]);
  const counts = useMemo(() => statusCounts(all), [all]);
  const visible = useMemo(() => filterBookings(all, filters), [all, filters]);

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title="Bookings"
        description="Every booking your agency has made, and where each one stands."
        actions={
          counts.failed > 0 ? (
            <Link to="/resolution" className={buttonVariants({ variant: 'outline', size: 'sm' })}>
              Resolution queue
              <Badge tone="destructive">{counts.failed}</Badge>
            </Link>
          ) : null
        }
      />

      <Card className="flex flex-col gap-4 p-4">
        <SegmentedControl
          label="Filter by status"
          // No counts until the list has loaded: "0" would claim there are no bookings.
          options={STATUS_FILTERS.map((option) =>
            bookings.data ? { ...option, count: counts[option.value] } : option,
          )}
          value={filters.status}
          onChange={(status) => setFilters({ ...filters, status })}
        />
        <div className="grid items-start gap-3 sm:grid-cols-3">
          <div className="sm:col-span-2">
            <Input
              label="Find a booking"
              placeholder="PNR, reference, traveller or place"
              value={filters.query}
              onChange={(event) => setFilters({ ...filters, query: event.target.value })}
            />
          </div>
          <Select
            label="Product"
            value={filters.product}
            onChange={(event) =>
              setFilters({ ...filters, product: event.target.value as ProductKind | 'all' })
            }
          >
            <option value="all">Flights and buses</option>
            <option value="flight">Flights</option>
            <option value="bus">Buses</option>
          </Select>
        </div>
      </Card>

      {bookings.isPending ? <ListSkeleton /> : null}

      {bookings.isError ? (
        <ErrorState
          {...describeError(bookings.error)}
          onRetry={() => void bookings.refetch()}
          retrying={bookings.isFetching}
        />
      ) : null}

      {bookings.data && all.length === 0 ? (
        <EmptyState
          size="page"
          headingLevel={2}
          icon={<Ticket aria-hidden="true" className="h-5 w-5" />}
          title="No bookings yet"
          action={
            <Link to="/search/flights" className={buttonVariants({ size: 'sm' })}>
              Search flights
            </Link>
          }
        >
          Every booking your agency makes appears here, with its status and where it stands with the
          supplier.
        </EmptyState>
      ) : null}

      {bookings.data && all.length > 0 && visible.length === 0 ? (
        <Card>
          <EmptyState
            title="No bookings match"
            action={
              <Button variant="outline" size="sm" onClick={() => setFilters(NO_BOOKING_FILTERS)}>
                Clear filters
              </Button>
            }
          >
            {all.length} bookings exist; the filters are hiding all of them.
          </EmptyState>
        </Card>
      ) : null}

      {visible.length > 0 ? <BookingsTable bookings={visible} /> : null}
    </div>
  );
}

function BookingsTable({ bookings }: { bookings: BookingListItem[] }) {
  return (
    <Card className="overflow-hidden">
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>Booking</TableHead>
            <TableHead>Traveller</TableHead>
            <TableHead>Route</TableHead>
            <TableHead>Departs</TableHead>
            <TableHead>Status</TableHead>
            <TableHead className="text-right">Sell</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {bookings.map((booking) => {
            const Icon = booking.product === 'flight' ? Plane : Bus;
            const status = STATUS_DISPLAY[booking.status];

            return (
              <TableRow key={booking.reference}>
                <TableCell>
                  <Link
                    to={`/bookings/${booking.reference}`}
                    className="font-medium text-primary underline-offset-4 hover:underline"
                  >
                    {booking.reference}
                  </Link>
                  {booking.pnr ? (
                    <p className="text-xs text-muted-foreground">PNR {booking.pnr}</p>
                  ) : null}
                </TableCell>
                <TableCell>
                  {booking.leadTraveller}
                  {booking.travellerCount > 1 ? (
                    <span className="text-muted-foreground"> +{booking.travellerCount - 1}</span>
                  ) : null}
                </TableCell>
                <TableCell>
                  <span className="inline-flex items-center gap-2 whitespace-nowrap">
                    <Icon aria-hidden="true" className="h-4 w-4 text-muted-foreground" />
                    {booking.origin}
                    <span className="sr-only">to</span>
                    <span aria-hidden="true" className="text-muted-foreground">
                      →
                    </span>
                    {booking.destination}
                  </span>
                </TableCell>
                <TableCell className="whitespace-nowrap text-muted-foreground">
                  {formatDeparture(booking.departsAt)}
                </TableCell>
                <TableCell>
                  <Badge tone={status.tone}>{status.label}</Badge>
                </TableCell>
                <TableCell className="text-right tabular-nums">
                  {formatMoneyShort(booking.sellMinor, booking.currency)}
                </TableCell>
              </TableRow>
            );
          })}
        </TableBody>
      </Table>
    </Card>
  );
}

function ListSkeleton() {
  return (
    <Card
      aria-busy="true"
      aria-label="Loading bookings"
      className="flex flex-col divide-y divide-border"
    >
      {[0, 1, 2, 3, 4].map((row) => (
        <div key={row} className="flex items-center gap-4 px-5 py-4">
          <Skeleton className="h-4 w-28" />
          <Skeleton className="h-4 flex-1" />
          <Skeleton className="h-5 w-24" />
        </div>
      ))}
    </Card>
  );
}
