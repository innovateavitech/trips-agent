import { CheckCircle2 } from 'lucide-react';
import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { Alert, EmptyState, ErrorState, Skeleton } from '@trips/ui';
import { formatMoneyShort } from '@trips/utils';
import { describeError } from '../../../api/errors';
import { PageHeader } from '../../../shell/page-header';
import { useBooking, useBookings, useResolveBooking } from '../bookings-api';
import { ResolutionCard } from '../components/resolution-card';

/**
 * #54 — the resolution queue. Every booking here is a customer who has paid and
 * received nothing yet, so it leads with the money at risk and is never buried
 * under the ordinary list.
 */
export function ResolutionPage() {
  const bookings = useBookings();
  const [notice, setNotice] = useState<string | null>(null);

  const failed = (bookings.data ?? []).filter((booking) => booking.status === 'failed');
  const atRisk = failed.reduce((sum, booking) => sum + booking.sellMinor, 0);
  const currency = failed[0]?.currency ?? 'NGN';

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title="Resolution queue"
        description="Bookings the airline or bus operator did not confirm. Each one is a customer's money — decide what happens to it."
      />

      {notice ? (
        <Alert tone="success" title="Done">
          {notice}
        </Alert>
      ) : null}

      {bookings.isPending ? <Skeleton className="h-40 w-full" /> : null}

      {bookings.isError ? (
        <ErrorState
          {...describeError(bookings.error)}
          onRetry={() => void bookings.refetch()}
          retrying={bookings.isFetching}
        />
      ) : null}

      {bookings.data && failed.length === 0 ? (
        <EmptyState
          size="page"
          headingLevel={2}
          icon={<CheckCircle2 aria-hidden="true" className="h-5 w-5" />}
          title="Nothing needs a decision"
        >
          When a supplier does not confirm a booking, it appears here first, with the money at risk
          and what you can do about it.
        </EmptyState>
      ) : null}

      {failed.length > 0 ? (
        <>
          <Alert
            tone="destructive"
            title={`${formatMoneyShort(atRisk, currency)} is waiting on you`}
          >
            {failed.length === 1 ? 'One booking' : `${failed.length} bookings`} the supplier did not
            confirm. Your customers have paid, and nothing has been issued.
          </Alert>
          <ul className="flex flex-col gap-4">
            {failed.map((booking) => (
              <li key={booking.reference}>
                <ResolutionItem reference={booking.reference} onDone={setNotice} />
              </li>
            ))}
          </ul>
        </>
      ) : null}
    </div>
  );
}

function ResolutionItem({
  reference,
  onDone,
}: {
  reference: string;
  onDone: (message: string) => void;
}) {
  const booking = useBooking(reference);
  const resolve = useResolveBooking();
  const navigate = useNavigate();

  if (booking.isPending) return <Skeleton className="h-40 w-full" />;

  if (booking.isError) {
    return (
      <ErrorState
        {...describeError(booking.error)}
        onRetry={() => void booking.refetch()}
        retrying={booking.isFetching}
      />
    );
  }

  const detail = booking.data;

  return (
    <>
      {resolve.isError ? (
        <ErrorState title="That did not go through" detail={describeError(resolve.error).detail} />
      ) : null}
      <ResolutionCard
        booking={detail}
        resolving={resolve.isPending ? (resolve.variables?.action ?? null) : null}
        onResolve={(action) =>
          resolve.mutate(
            { reference, action },
            {
              onSuccess: () =>
                onDone(
                  action === 'refund'
                    ? `Refunded ${formatMoneyShort(detail.failure?.atRiskMinor ?? detail.sellMinor, detail.currency)} for ${reference}.`
                    : `Asked ${detail.carrier} to issue ${reference} again. It is back in Bookings, awaiting its ticket.`,
                ),
            },
          )
        }
        onSubstitute={() =>
          navigate(detail.product === 'flight' ? '/search/flights' : '/search/buses')
        }
      />
    </>
  );
}
