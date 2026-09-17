import { ArrowLeft, Bus, Plane, Ticket } from 'lucide-react';
import type { ReactNode } from 'react';
import { Link, useParams } from 'react-router-dom';
import {
  Alert,
  Avatar,
  Badge,
  EmptyState,
  ErrorState,
  LoadingState,
  buttonVariants,
} from '@trips/ui';
import { formatMoneyShort } from '@trips/utils';
import { ApiError, describeError } from '../../../api/errors';
import { STATUS_DISPLAY, describeTimeLeft } from '../../dashboard/booking-display';
import { formatClock, formatDay } from '../../search/search-rules';
import { useBooking } from '../bookings-api';
import { describeDuration, formatDateOfBirth, splitFareEvenly } from '../bookings-rules';
import type { BookingDetail, BookingSegment } from '../types';

const TRAVELLER_TYPE: Record<BookingDetail['travellers'][number]['type'], string> = {
  ADT: 'Adult',
  CHD: 'Child',
  INF: 'Infant',
};

const timeFormat = new Intl.DateTimeFormat('en-NG', {
  day: 'numeric',
  month: 'short',
  hour: '2-digit',
  minute: '2-digit',
  hourCycle: 'h23',
  timeZone: 'Africa/Lagos',
});

const shortDayFormat = new Intl.DateTimeFormat('en-NG', {
  day: 'numeric',
  month: 'short',
  timeZone: 'UTC',
});

/** `'2026-09-19T11:00'` → `'19 Sept'`. */
function shortDay(localDateTime: string): string {
  return shortDayFormat.format(new Date(`${localDateTime.slice(0, 10)}T00:00:00Z`));
}

/**
 * A section card in this page's design language — matching Home and Travel:
 * heavily rounded, a hairline border, no header divider. NOT the older
 * `Card`/`CardHeader`/`CardTitle` from `@trips/ui`, which belongs to the
 * app's pre-redesign surfaces and reads visibly boxier next to these.
 */
function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <div className="rounded-[2rem] border border-border-subtle bg-card p-5">
      <h2 className="mb-5 text-base font-medium text-foreground">{title}</h2>
      {children}
    </div>
  );
}

/** #54 — one booking, in full: who, where, what it cost, and everything that has happened to it. */
export function BookingDetailPage() {
  const { orderId = '' } = useParams<{ orderId: string }>();
  const booking = useBooking(orderId);

  if (booking.isPending) return <LoadingState size="page" label="Loading the booking" />;

  if (booking.isError) {
    if (booking.error instanceof ApiError && booking.error.status === 404) {
      return (
        <EmptyState
          size="page"
          headingLevel={1}
          icon={<Ticket aria-hidden="true" className="h-5 w-5" />}
          title="We could not find that booking"
          action={
            <Link to="/bookings" className={buttonVariants({ size: 'sm' })}>
              All bookings
            </Link>
          }
        >
          It may belong to another agency, or the reference may be mistyped.
        </EmptyState>
      );
    }

    return (
      <ErrorState
        {...describeError(booking.error)}
        onRetry={() => void booking.refetch()}
        retrying={booking.isFetching}
      />
    );
  }

  return <BookingView booking={booking.data} />;
}

function BookingView({ booking }: { booking: BookingDetail }) {
  const status = STATUS_DISPLAY[booking.status];
  const timeLeft = booking.ticketTimeLimit
    ? describeTimeLeft(booking.ticketTimeLimit, new Date())
    : null;
  const travellerFaresMinor = splitFareEvenly(booking.price.sellMinor, booking.travellers.length);

  return (
    <div className="flex flex-col gap-6">
      <Link
        to="/bookings"
        className="inline-flex w-fit items-center gap-1 text-sm text-muted-foreground hover:text-foreground"
      >
        <ArrowLeft aria-hidden="true" className="h-4 w-4" />
        Trips
      </Link>

      <div className="flex flex-wrap items-center justify-between gap-4">
        <div className="flex items-center gap-3">
          <Avatar name={booking.leadTraveller} size={56} tone="muted" />
          <div className="flex flex-col">
            <h1 className="text-2xl font-semibold tracking-tight text-foreground">
              {booking.leadTraveller}
            </h1>
            <p className="text-sm text-muted-foreground">
              {booking.customerKind === 'business' ? 'Business' : 'Individual'}
            </p>
          </div>
        </div>
        <Badge tone={status.tone}>{status.label}</Badge>
      </div>

      {booking.status === 'awaiting_ticket' ? (
        <div className="flex items-center gap-3 rounded-[2rem] border border-border-subtle bg-card p-5">
          <span
            aria-hidden="true"
            className="h-5 w-5 shrink-0 animate-spin rounded-full border-2 border-primary border-t-transparent motion-reduce:animate-none"
          />
          <div className="flex flex-col gap-1">
            <p className="text-sm font-medium text-foreground">
              We&rsquo;re confirming with {booking.carrier}
            </p>
            <p className="text-sm text-muted-foreground">
              Paid. The ticket is being issued — usually within a minute, sometimes longer. This
              page updates by itself.
              {timeLeft && (timeLeft.urgency === 'urgent' || timeLeft.urgency === 'soon')
                ? ` The supplier's deadline to issue: ${timeLeft.label}.`
                : null}
            </p>
          </div>
        </div>
      ) : null}

      {booking.status === 'failed' && booking.failure ? (
        <Alert
          tone="destructive"
          title={`${booking.carrier} did not confirm this booking`}
          action={
            <Link to="/resolution" className={buttonVariants({ size: 'sm', variant: 'outline' })}>
              Open the resolution queue
            </Link>
          }
        >
          {booking.failure.reason} Nothing was issued —{' '}
          {formatMoneyShort(booking.failure.atRiskMinor, booking.currency)} of your customer&rsquo;s
          money is waiting on your decision: try again, find another fare, or refund.
        </Alert>
      ) : null}

      <div className="grid items-start gap-5 lg:grid-cols-3">
        <div className="flex flex-col gap-5 lg:col-span-2">
          <Section
            title={`Itinerary — ${shortDay(booking.segments[0]?.departsAt ?? booking.departsAt)} – ${shortDay(
              booking.segments[booking.segments.length - 1]?.arrivesAt ??
                booking.segments[0]?.departsAt ??
                booking.departsAt,
            )}`}
          >
            <ItineraryTimeline product={booking.product} segments={booking.segments} />
          </Section>

          <Section title="Payment summary">
            <dl className="flex flex-col gap-6 text-sm">
              <div className="flex items-center justify-between gap-3">
                <dt className="text-foreground">Passengers × {booking.travellers.length}</dt>
                <dd className="tabular-nums text-foreground">
                  {formatMoneyShort(travellerFaresMinor[0] ?? 0, booking.currency)}
                </dd>
              </div>
              {booking.price.margin ? (
                <>
                  <div className="flex items-center justify-between gap-3">
                    <dt className="text-foreground">Net total</dt>
                    <dd className="tabular-nums text-foreground">
                      {formatMoneyShort(booking.price.margin.netMinor, booking.currency)}
                    </dd>
                  </div>
                  <div className="flex items-center justify-between gap-3">
                    <dt className="text-foreground">Your margin</dt>
                    <dd className="tabular-nums text-foreground">
                      {formatMoneyShort(booking.price.margin.markupMinor, booking.currency)}
                    </dd>
                  </div>
                </>
              ) : null}
              <div className="flex items-center justify-between gap-3">
                <dt className="text-lg font-semibold text-foreground">Total for all passengers</dt>
                <dd className="text-lg font-semibold tabular-nums text-foreground">
                  {formatMoneyShort(booking.price.sellMinor, booking.currency)}
                </dd>
              </div>
            </dl>
            <p className="mt-4 text-xs text-muted-foreground">
              Paid {booking.paidFrom === 'wallet' ? 'from your wallet' : "on the customer's card"}
            </p>
          </Section>

          <Section title={`Travellers (${booking.travellers.length})`}>
            <ul className="flex flex-col gap-6">
              {booking.travellers.map((traveller, index) => (
                <li key={index} className="flex flex-wrap items-center justify-between gap-3">
                  <div className="flex flex-col">
                    <p className="text-sm text-foreground">{traveller.name}</p>
                    <p className="text-xs text-muted-foreground">
                      {[
                        TRAVELLER_TYPE[traveller.type],
                        traveller.passportNumber,
                        traveller.dateOfBirth ? formatDateOfBirth(traveller.dateOfBirth) : null,
                      ]
                        .filter(Boolean)
                        .join(' · ')}
                    </p>
                  </div>
                  <div className="flex flex-col items-end gap-0.5">
                    <p className="text-sm font-medium tabular-nums text-foreground">
                      {formatMoneyShort(travellerFaresMinor[index] ?? 0, booking.currency)}
                    </p>
                    <p className="text-xs tabular-nums text-muted-foreground">
                      {traveller.ticketNumber
                        ? `Ticket ${traveller.ticketNumber}`
                        : booking.product === 'flight'
                          ? 'Not issued yet'
                          : 'Seat on the manifest'}
                    </p>
                  </div>
                </li>
              ))}
            </ul>
          </Section>
        </div>

        <div className="flex flex-col gap-5">
          <Section title="Customer details">
            <dl className="flex flex-col gap-4 text-sm">
              <div className="flex items-center justify-between gap-3">
                <dt className="text-muted-foreground">Name</dt>
                <dd className="text-right font-medium text-foreground underline underline-offset-4">
                  {booking.leadTraveller}
                </dd>
              </div>
              <div className="flex items-center justify-between gap-3">
                <dt className="text-muted-foreground">Customer type</dt>
                <dd className="text-right text-foreground">
                  {booking.customerKind === 'business' ? 'Business' : 'Individual'}
                </dd>
              </div>
            </dl>
          </Section>

          <Section title="Booking details">
            <dl className="flex flex-col gap-4 text-sm">
              <div className="flex items-center justify-between gap-3">
                <dt className="text-muted-foreground">Booking PNR</dt>
                <dd className="text-right font-medium text-foreground underline underline-offset-4">
                  {booking.pnr ?? '—'}
                </dd>
              </div>
              <div className="flex items-center justify-between gap-3">
                <dt className="text-muted-foreground">Airline</dt>
                <dd className="text-right text-foreground">{booking.carrier}</dd>
              </div>
              {booking.cabinClass ? (
                <div className="flex items-center justify-between gap-3">
                  <dt className="text-muted-foreground">Cabin class</dt>
                  <dd className="text-right text-foreground">{booking.cabinClass}</dd>
                </div>
              ) : null}
              <div className="flex items-start justify-between gap-3">
                <dt className="shrink-0 text-muted-foreground">Contact email</dt>
                <dd className="min-w-0 break-all text-right text-foreground">
                  {booking.contactEmail ?? '—'}
                </dd>
              </div>
              <div className="flex items-center justify-between gap-3">
                <dt className="text-muted-foreground">Phone number</dt>
                <dd className="text-right text-foreground">{booking.contactPhone ?? '—'}</dd>
              </div>
              <div className="flex items-center justify-between gap-3">
                <dt className="text-muted-foreground">Booking date</dt>
                <dd className="text-right text-foreground">
                  {timeFormat.format(new Date(booking.bookedAt))}
                </dd>
              </div>
            </dl>
          </Section>
        </div>
      </div>
    </div>
  );
}

/** The big origin-to-destination timeline: times, dates, and how the trip gets there. */
function ItineraryTimeline({
  product,
  segments,
}: {
  product: BookingDetail['product'];
  segments: BookingSegment[];
}) {
  const first = segments[0];
  const last = segments[segments.length - 1];
  if (!first || !last) return null;

  const RouteIcon = product === 'flight' ? Plane : Bus;
  const stops =
    segments.length === 1
      ? 'Direct'
      : `${segments.length - 1} stop${segments.length > 2 ? 's' : ''}`;

  return (
    <div className="flex items-center gap-4">
      <div className="flex flex-col gap-1">
        <p className="text-3xl font-semibold tabular-nums text-foreground">
          {formatClock(first.departsAt)}
        </p>
        <p className="text-base font-medium text-foreground">{first.origin}</p>
        <p className="text-sm text-muted-foreground">{formatDay(first.departsAt)}</p>
      </div>

      <div className="flex flex-1 flex-col items-center gap-1.5 px-2">
        <p className="text-sm text-muted-foreground">
          {last.arrivesAt ? describeDuration(first.departsAt, last.arrivesAt) : '—'}
        </p>
        <div className="flex w-full items-center gap-2">
          <span aria-hidden="true" className="h-px flex-1 bg-border" />
          <span
            aria-hidden="true"
            className="flex size-7 shrink-0 items-center justify-center rounded-full bg-primary text-primary-foreground"
          >
            <RouteIcon aria-hidden="true" className="h-3.5 w-3.5" />
          </span>
          <span aria-hidden="true" className="h-px flex-1 bg-border" />
        </div>
        <p className="text-sm text-muted-foreground">{stops}</p>
      </div>

      <div className="flex flex-col items-end gap-1">
        <p className="text-3xl font-semibold tabular-nums text-foreground">
          {last.arrivesAt ? formatClock(last.arrivesAt) : '—'}
        </p>
        <p className="text-base font-medium text-foreground">{last.destination}</p>
        <p className="text-sm text-muted-foreground">
          {last.arrivesAt ? formatDay(last.arrivesAt) : ''}
        </p>
      </div>
    </div>
  );
}
