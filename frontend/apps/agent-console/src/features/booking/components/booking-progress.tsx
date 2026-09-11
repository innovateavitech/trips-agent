import { Link } from 'react-router-dom';
import { Alert, Badge, Button, Card, ErrorState, LoadingState, buttonVariants } from '@trips/ui';
import { describeError } from '../../../api/errors';
import { useBookingProgress } from '../booking-api';
import type { BookingProgress as Progress } from '../types';

/**
 * #53, the last step — what happened after the agent paid, told honestly. While
 * the supplier is issuing it says exactly that, and never "booked" before the
 * ticket exists; the booking is already in Bookings either way.
 */
export function BookingProgress({ reference, carrier }: { reference: string; carrier: string }) {
  const progress = useBookingProgress(reference);

  if (progress.isPending) {
    return (
      <Card className="p-6">
        <LoadingState label="Placing your booking" />
      </Card>
    );
  }

  if (progress.isError) {
    return (
      <ErrorState
        {...describeError(progress.error)}
        onRetry={() => void progress.refetch()}
        retrying={progress.isFetching}
      />
    );
  }

  return <BookingProgressCard progress={progress.data} reference={reference} carrier={carrier} />;
}

export function BookingProgressCard({
  progress,
  reference,
  carrier,
}: {
  progress: Progress;
  reference: string;
  carrier: string;
}) {
  if (progress.status === 'failed') {
    return (
      <Alert
        tone="destructive"
        title={`${carrier} did not confirm this booking`}
        action={
          <Link to="/resolution" className={buttonVariants({ size: 'sm', variant: 'outline' })}>
            Open the resolution queue
          </Link>
        }
      >
        Nothing was issued. The booking is waiting in your resolution queue, where you can try
        again, find another fare, or refund your customer.
      </Alert>
    );
  }

  if (progress.status === 'awaiting_ticket') {
    return (
      <Card role="status" aria-live="polite" className="flex flex-col gap-3 p-6">
        <div className="flex items-center gap-3">
          <span
            aria-hidden="true"
            className="h-5 w-5 animate-spin rounded-full border-2 border-primary border-t-transparent motion-reduce:animate-none"
          />
          <h2 className="text-lg font-semibold text-foreground">
            We&rsquo;re confirming with {carrier}
          </h2>
        </div>
        <p className="text-sm text-muted-foreground">
          Paid. The ticket is being issued — usually within a minute, sometimes longer. This page
          updates by itself, and you can leave it: the booking is already in Bookings.
        </p>
        <div>
          <Link
            to={`/bookings/${reference}`}
            className={buttonVariants({ variant: 'outline', size: 'sm' })}
          >
            Open booking {reference}
          </Link>
        </div>
      </Card>
    );
  }

  return (
    <Card role="status" aria-live="polite" className="flex flex-col gap-4 p-6">
      <div>
        <Badge tone="success">Ticketed</Badge>
      </div>
      <h2 className="text-lg font-semibold text-foreground">Booking confirmed</h2>
      <div>
        <p className="text-xs text-muted-foreground">Supplier reference (PNR)</p>
        <p className="text-3xl font-semibold tracking-widest text-foreground">{progress.pnr}</p>
      </div>
      <div className="flex flex-wrap gap-2">
        <Link to={`/bookings/${reference}`} className={buttonVariants({ size: 'sm' })}>
          View booking
        </Link>
        <Button variant="outline" size="sm" disabled>
          Voucher
        </Button>
        <Link to="/search/flights" className={buttonVariants({ variant: 'ghost', size: 'sm' })}>
          Book another
        </Link>
      </div>
      <p className="text-xs text-muted-foreground">
        The voucher in your own branding arrives with the documents service.
      </p>
    </Card>
  );
}
