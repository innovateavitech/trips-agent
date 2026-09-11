import { Ticket } from 'lucide-react';
import { Link, useParams } from 'react-router-dom';
import {
  Alert,
  Badge,
  Card,
  CardHeader,
  CardTitle,
  EmptyState,
  ErrorState,
  LoadingState,
  buttonVariants,
} from '@trips/ui';
import { formatMoneyShort } from '@trips/utils';
import { ApiError, describeError } from '../../../api/errors';
import { PageHeader } from '../../../shell/page-header';
import { STATUS_DISPLAY, describeTimeLeft, formatDeparture } from '../../dashboard/booking-display';
import { formatClock, formatDay } from '../../search/search-rules';
import { useBooking, useBookingDocuments, useReissueDocument } from '../bookings-api';
import { DocumentsCard } from '../components/documents-card';
import type { BookingDetail } from '../types';

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

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title={`Booking ${booking.reference}`}
        description={`${booking.origin} → ${booking.destination} · ${booking.carrier} · ${formatDeparture(booking.departsAt)}`}
        actions={<Badge tone={status.tone}>{status.label}</Badge>}
      />

      {booking.status === 'awaiting_ticket' ? (
        <Alert tone="warning" title={`We're confirming with ${booking.carrier}`}>
          Paid, and waiting for the ticket to be issued. This page updates by itself.
          {timeLeft ? ` The supplier's deadline to issue: ${timeLeft.label}.` : null}
        </Alert>
      ) : null}

      {booking.status === 'failed' && booking.failure ? (
        <Alert
          tone="destructive"
          title="The supplier did not confirm this booking"
          action={
            <Link to="/resolution" className={buttonVariants({ size: 'sm', variant: 'outline' })}>
              Decide in the resolution queue
            </Link>
          }
        >
          {booking.failure.reason} {formatMoneyShort(booking.failure.atRiskMinor, booking.currency)}{' '}
          of your customer&rsquo;s money is waiting on your decision.
        </Alert>
      ) : null}

      <div className="grid items-start gap-6 lg:grid-cols-3">
        <div className="flex flex-col gap-6 lg:col-span-2">
          <Card>
            <CardHeader>
              <CardTitle>Itinerary</CardTitle>
            </CardHeader>
            <ul className="divide-y divide-border border-t border-border">
              {booking.segments.map((segment, index) => (
                <li
                  key={index}
                  className="flex flex-wrap items-center justify-between gap-3 px-5 py-4"
                >
                  <div>
                    <p className="text-sm font-medium text-foreground">
                      {segment.origin} → {segment.destination}
                    </p>
                    <p className="text-xs text-muted-foreground">{segment.carrier}</p>
                  </div>
                  <p className="text-sm tabular-nums text-foreground">
                    {formatDay(segment.departsAt)} · {formatClock(segment.departsAt)}
                    {segment.arrivesAt ? `–${formatClock(segment.arrivesAt)}` : ''}
                  </p>
                </li>
              ))}
            </ul>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>Travellers</CardTitle>
            </CardHeader>
            <ul className="divide-y divide-border border-t border-border">
              {booking.travellers.map((traveller, index) => (
                <li
                  key={index}
                  className="flex flex-wrap items-center justify-between gap-3 px-5 py-3"
                >
                  <p className="text-sm text-foreground">
                    {traveller.name}{' '}
                    <span className="text-xs text-muted-foreground">
                      {TRAVELLER_TYPE[traveller.type]}
                    </span>
                  </p>
                  <p className="text-xs tabular-nums text-muted-foreground">
                    {traveller.ticketNumber
                      ? `Ticket ${traveller.ticketNumber}`
                      : booking.product === 'flight'
                        ? 'Not issued yet'
                        : 'Seat on the manifest'}
                  </p>
                </li>
              ))}
            </ul>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>History</CardTitle>
            </CardHeader>
            <ol className="flex flex-col gap-4 border-t border-border px-5 py-4">
              {booking.timeline.map((entry, index) => (
                <li key={index} className="flex gap-3">
                  <span
                    aria-hidden="true"
                    className="mt-1.5 h-2 w-2 shrink-0 rounded-full bg-muted-foreground"
                  />
                  <div className="flex flex-col gap-0.5">
                    <p className="text-sm text-foreground">{entry.note}</p>
                    <p className="text-xs text-muted-foreground">
                      {timeFormat.format(new Date(entry.at))} · {STATUS_DISPLAY[entry.status].label}
                    </p>
                  </div>
                </li>
              ))}
            </ol>
          </Card>
        </div>

        <div className="flex flex-col gap-6">
          <Card className="flex flex-col gap-3 p-5">
            <p className="text-xs text-muted-foreground">Your customer paid</p>
            <p className="text-2xl font-semibold tabular-nums text-foreground">
              {formatMoneyShort(booking.price.sellMinor, booking.currency)}
            </p>
            {booking.price.margin ? (
              <dl className="flex flex-col gap-1 border-t border-border pt-3 text-sm">
                <div className="flex justify-between text-muted-foreground">
                  <dt>Net</dt>
                  <dd className="tabular-nums">
                    {formatMoneyShort(booking.price.margin.netMinor, booking.currency)}
                  </dd>
                </div>
                <div className="flex justify-between font-medium text-success-subtle-foreground">
                  <dt>Your margin</dt>
                  <dd className="tabular-nums">
                    {formatMoneyShort(booking.price.margin.markupMinor, booking.currency)}
                  </dd>
                </div>
              </dl>
            ) : null}
            <p className="text-xs text-muted-foreground">
              Paid {booking.paidFrom === 'wallet' ? 'from your wallet' : "on the customer's card"}
            </p>
          </Card>

          <Card className="flex flex-col gap-3 p-5">
            <p className="text-xs text-muted-foreground">Supplier reference</p>
            <p className="text-xl font-semibold tracking-widest text-foreground">
              {booking.pnr ?? '—'}
            </p>
            <p className="text-xs text-muted-foreground">
              Booked {timeFormat.format(new Date(booking.bookedAt))}
            </p>
          </Card>

          <BookingDocuments booking={booking} />
        </div>
      </div>
    </div>
  );
}

/** The booking's invoices and vouchers, with download and reissue (#46). */
function BookingDocuments({ booking }: { booking: BookingDetail }) {
  const documents = useBookingDocuments(booking.reference);
  const reissue = useReissueDocument(booking.reference);

  const problem = documents.isError
    ? describeError(documents.error).title
    : reissue.isError
      ? describeError(reissue.error).title
      : null;

  return (
    <DocumentsCard
      documents={documents.data}
      loading={documents.isPending}
      problem={problem}
      ticketed={booking.status === 'ticketed'}
      reissuingId={reissue.isPending ? (reissue.variables ?? null) : null}
      onReissue={(document) => reissue.mutate(document.id)}
    />
  );
}
