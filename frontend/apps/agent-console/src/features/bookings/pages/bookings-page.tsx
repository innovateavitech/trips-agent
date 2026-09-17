import { Bus, Plane, Ticket } from 'lucide-react';
import { useState } from 'react';
import { Link } from 'react-router-dom';
import {
  Avatar,
  Badge,
  BookTravelIcon,
  Button,
  ChevronDownIcon,
  ConfigurationIcon,
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuTrigger,
  EmptyState,
  ErrorState,
  GlobalSearchInput,
  Input,
  SegmentedControl,
  Skeleton,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
  buttonVariants,
  cn,
} from '@trips/ui';
import { formatMoneyShort } from '@trips/utils';
import { describeError } from '../../../api/errors';
import { PageHeader } from '../../../shell/page-header';
import { formatShortDate, formatShortDateTime } from '../../dashboard/booking-display';
import { useBookings } from '../bookings-api';
import {
  TRIP_FILTERS,
  dayWithinRange,
  filterByCustomerName,
  filterByTravelStage,
  statusCounts,
  type TravelStage,
} from '../bookings-rules';
import type { BookingListItem } from '../types';

interface DateRange {
  from: string;
  to: string;
}

const NO_RANGE: DateRange = { from: '', to: '' };

/**
 * #54 — every trip an agency's customers have taken, grouped one row per
 * booking's customer. "All trips"/Active/Upcoming/Completed/Requested are
 * where a trip stands on the calendar; the (rarer) "Needs decision" queue
 * below is where money is actually at risk, and stays its own thing so it is
 * never buried under a tab nobody happens to be on.
 */
export function BookingsPage() {
  const bookings = useBookings();
  const [stage, setStage] = useState<TravelStage | 'all'>('all');
  const [name, setName] = useState('');
  const [bookingRange, setBookingRange] = useState<DateRange>(NO_RANGE);
  const [startRange, setStartRange] = useState<DateRange>(NO_RANGE);
  const [endRange, setEndRange] = useState<DateRange>(NO_RANGE);

  const now = new Date();
  const all = bookings.data ?? [];
  const counts = statusCounts(all);

  const visible = filterByCustomerName(
    filterByTravelStage(all, stage, now).filter(
      (booking) =>
        dayWithinRange(booking.bookedAt, bookingRange.from, bookingRange.to) &&
        dayWithinRange(booking.departsAt, startRange.from, startRange.to) &&
        dayWithinRange(booking.arrivesAt ?? booking.departsAt, endRange.from, endRange.to),
    ),
    name,
  );

  const filtersActive = Boolean(
    bookingRange.from ||
    bookingRange.to ||
    startRange.from ||
    startRange.to ||
    endRange.from ||
    endRange.to ||
    name.trim(),
  );

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title="Travel"
        actions={
          <>
            <Link
              to="/pricing"
              className={cn(
                buttonVariants({ variant: 'outline', size: 'sm' }),
                'gap-2 rounded-full',
              )}
            >
              <ConfigurationIcon size={16} />
              Configuration
            </Link>
            {counts.failed > 0 ? (
              <Link to="/resolution" className={buttonVariants({ variant: 'outline', size: 'sm' })}>
                Resolution queue
                <Badge tone="destructive">{counts.failed}</Badge>
              </Link>
            ) : null}
            <Link to="/search/flights" className={buttonVariants({ size: 'md', radius: 'lg' })}>
              <BookTravelIcon size={16} />
              Book travel
            </Link>
          </>
        }
      />

      <div className="border-b border-border-subtle">
        <SegmentedControl
          label="Filter by trip stage"
          appearance="underline"
          options={TRIP_FILTERS}
          value={stage}
          onChange={setStage}
        />
      </div>

      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="flex flex-wrap gap-2">
          <DateRangeMenu label="Booking date" range={bookingRange} onChange={setBookingRange} />
          <DateRangeMenu label="Start date" range={startRange} onChange={setStartRange} />
          <DateRangeMenu label="End date" range={endRange} onChange={setEndRange} />
        </div>
        <GlobalSearchInput
          className="w-full max-w-[320px]"
          placeholder="Filter by name"
          aria-label="Filter by name"
          value={name}
          onChange={(event) => setName(event.target.value)}
        />
      </div>

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
          title="No trips yet"
          action={
            <Link to="/search/flights" className={buttonVariants({ size: 'sm' })}>
              Search flights
            </Link>
          }
        >
          Every trip your agency books appears here, by customer, with where it stands.
        </EmptyState>
      ) : null}

      {bookings.data && all.length > 0 && visible.length === 0 ? (
        <EmptyState
          title="No trips match"
          action={
            filtersActive ? (
              <Button
                variant="outline"
                size="sm"
                onClick={() => {
                  setBookingRange(NO_RANGE);
                  setStartRange(NO_RANGE);
                  setEndRange(NO_RANGE);
                  setName('');
                }}
              >
                Clear filters
              </Button>
            ) : undefined
          }
        >
          {all.length} trips exist; the filters are hiding all of them.
        </EmptyState>
      ) : null}

      {visible.length > 0 ? <TravelTable bookings={visible} /> : null}
    </div>
  );
}

function DateRangeMenu({
  label,
  range,
  onChange,
}: {
  label: string;
  range: DateRange;
  onChange: (range: DateRange) => void;
}) {
  const active = Boolean(range.from || range.to);

  return (
    <DropdownMenu>
      <DropdownMenuTrigger
        className={cn(
          buttonVariants({ variant: 'outline', size: 'sm' }),
          'gap-2 rounded-full',
          active ? 'border-primary text-primary' : 'text-muted-foreground',
        )}
      >
        {label}
        <ChevronDownIcon size={16} />
      </DropdownMenuTrigger>
      <DropdownMenuContent align="start" className="w-64 p-3">
        <div className="flex flex-col gap-3">
          <Input
            type="date"
            label="From"
            value={range.from}
            max={range.to || undefined}
            onChange={(event) => onChange({ ...range, from: event.target.value })}
          />
          <Input
            type="date"
            label="To"
            value={range.to}
            min={range.from || undefined}
            onChange={(event) => onChange({ ...range, to: event.target.value })}
          />
          {active ? (
            <Button type="button" variant="ghost" size="sm" onClick={() => onChange(NO_RANGE)}>
              Clear
            </Button>
          ) : null}
        </div>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

const headerCellClasses =
  'h-10 bg-muted text-xs font-medium normal-case tracking-normal text-muted-foreground first:rounded-l-lg last:rounded-r-lg';

function TravelTable({ bookings }: { bookings: BookingListItem[] }) {
  return (
    <div className="overflow-hidden rounded-xl">
      <Table>
        <TableHeader>
          <TableRow className="border-none hover:bg-transparent">
            <TableHead className={headerCellClasses}>Customer</TableHead>
            <TableHead className={headerCellClasses}>Travellers</TableHead>
            <TableHead className={headerCellClasses}>Route</TableHead>
            <TableHead className={headerCellClasses}>Booking date</TableHead>
            <TableHead className={headerCellClasses}>Booking start date</TableHead>
            <TableHead className={headerCellClasses}>Booking end date</TableHead>
            <TableHead className={cn(headerCellClasses, 'text-right')}>Amount</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {bookings.map((booking) => {
            const RouteIcon = booking.product === 'flight' ? Plane : Bus;

            return (
              <TableRow key={booking.reference} className="border-border-subtle">
                <TableCell>
                  <div className="flex items-center gap-3">
                    <Avatar name={booking.leadTraveller} size={40} tone="muted" />
                    <div className="flex min-w-0 flex-col">
                      <Link
                        to={`/bookings/${booking.reference}`}
                        className="truncate font-medium text-foreground underline-offset-4 hover:text-primary hover:underline"
                      >
                        {booking.leadTraveller}
                      </Link>
                      <span className="text-xs text-muted-foreground">
                        {booking.customerKind === 'business' ? 'Business' : 'Individual'}
                      </span>
                    </div>
                  </div>
                </TableCell>
                <TableCell>
                  <span className="inline-flex size-7 items-center justify-center rounded-lg bg-muted text-sm font-semibold text-foreground">
                    {booking.travellerCount}
                  </span>
                </TableCell>
                <TableCell className="whitespace-nowrap text-muted-foreground">
                  <span className="inline-flex items-center gap-2">
                    <RouteIcon aria-hidden="true" className="h-4 w-4 text-muted-foreground" />
                    {booking.origin}
                    <span className="sr-only">to</span>
                    <span aria-hidden="true">→</span>
                    {booking.destination}
                  </span>
                </TableCell>
                <TableCell className="whitespace-nowrap text-foreground">
                  {formatShortDate(booking.bookedAt)}
                </TableCell>
                <TableCell className="whitespace-nowrap text-muted-foreground">
                  {formatShortDateTime(booking.departsAt)}
                </TableCell>
                <TableCell className="whitespace-nowrap text-muted-foreground">
                  {booking.arrivesAt ? formatShortDateTime(booking.arrivesAt) : '—'}
                </TableCell>
                <TableCell className="text-right">
                  <div className="flex flex-col items-end">
                    <span className="font-semibold tabular-nums text-foreground">
                      {formatMoneyShort(booking.sellMinor, booking.currency)}
                    </span>
                    <span className="text-xs text-muted-foreground">Booking total</span>
                  </div>
                </TableCell>
              </TableRow>
            );
          })}
        </TableBody>
      </Table>
    </div>
  );
}

function ListSkeleton() {
  return (
    <div
      aria-busy="true"
      aria-label="Loading trips"
      className="flex flex-col divide-y divide-border-subtle overflow-hidden rounded-xl border border-border-subtle"
    >
      {[0, 1, 2, 3, 4].map((row) => (
        <div key={row} className="flex items-center gap-4 px-4 py-4">
          <Skeleton className="h-10 w-10 rounded-full" />
          <Skeleton className="h-4 flex-1" />
          <Skeleton className="h-4 w-24" />
          <Skeleton className="h-4 w-20" />
        </div>
      ))}
    </div>
  );
}
