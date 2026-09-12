import { Link } from 'react-router-dom';
import { Alert, Badge, Button, Card, ErrorState, LoadingState, buttonVariants } from '@trips/ui';
import { describeError } from '../../../api/errors';
import { useBookingDocuments } from '../../bookings/bookings-api';
import { currentVoucher, documentHref } from '../../bookings/documents-rules';
import type { BookingDocument } from '../../bookings/types';
import { useBookingProgress } from '../booking-api';
import type { BookingProgress as Progress } from '../types';

/**
 * #53, the last step — what happened after the agent paid, told honestly. While
 * the supplier is issuing it says exactly that, and never "booked" before the
 * ticket exists; the booking is already in Bookings either way.
 */
export function BookingProgress({ reference, carrier }: { reference: string; carrier: string }) {
  const progress = useBookingProgress(reference);

  // Only once it is ticketed: that is when the Worker starts preparing the voucher (#46).
  const documents = useBookingDocuments(reference, {
    enabled: progress.data?.status === 'ticketed',
  });

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

  return (
    <BookingProgressCard
      progress={progress.data}
      reference={reference}
      carrier={carrier}
      voucher={currentVoucher(documents.data)}
    />
  );
}

export function BookingProgressCard({
  progress,
  reference,
  carrier,
  voucher = null,
}: {
  progress: Progress;
  reference: string;
  carrier: string;
  /** The voucher to link to, once there is one. It is prepared just after the ticket lands. */
  voucher?: BookingDocument | null;
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
        {voucher?.status === 'Ready' && voucher.downloadUrl ? (
          <a
            href={documentHref(voucher.downloadUrl)}
            download={voucher.fileName ?? true}
            target="_blank"
            rel="noreferrer"
            className={buttonVariants({ variant: 'outline', size: 'sm' })}
          >
            Voucher
          </a>
        ) : (
          <Button variant="outline" size="sm" disabled>
            Preparing voucher…
          </Button>
        )}
        <Link to="/search/flights" className={buttonVariants({ variant: 'ghost', size: 'sm' })}>
          Book another
        </Link>
      </div>
      <p className="text-xs text-muted-foreground">
        {voucher?.status === 'Ready'
          ? 'In your own branding. Your customer has been emailed it, with the invoice.'
          : 'Your voucher is being prepared in your own branding. It appears here in a moment.'}
      </p>
    </Card>
  );
}
