import { Ticket } from 'lucide-react';
import { useState } from 'react';
import { Link, useLocation } from 'react-router-dom';
import { Alert, EmptyState, ErrorState, buttonVariants } from '@trips/ui';
import { describeError } from '../../../api/errors';
import { PageHeader } from '../../../shell/page-header';
import { cx } from '../../search/class-names';
import { useConfirmPrice } from '../booking-api';
import {
  carrierOf,
  describeRoute,
  emptyTraveller,
  priceChanged,
  readDraft,
  travellerSlots,
} from '../booking-rules';
import { BookingProgress } from '../components/booking-progress';
import { PriceChangeDialog } from '../components/price-change-dialog';
import { ReviewStep } from '../components/review-step';
import { TravellerStep } from '../components/traveller-form';
import { TripSummary } from '../components/trip-summary';
import type { BookingDraft, PriceConfirmation, TravellerDetails } from '../types';

type Step = 'travellers' | 'review' | 'ticket';

const STEPS: ReadonlyArray<{ id: Step; label: string }> = [
  { id: 'travellers', label: 'Travellers' },
  { id: 'review', label: 'Review and pay' },
  { id: 'ticket', label: 'Ticket' },
];

/**
 * #53 — from a chosen fare to a ticket: travellers, the supplier's confirmed
 * price (and a re-consent if it moved), review and pay, then the ticket.
 *
 * Entered from a search result, never cold. With no fare in hand — a direct
 * link, or a reload after the browser dropped the navigation state — it says how
 * to start one rather than showing an empty form.
 */
export function BookingFlowPage() {
  const location = useLocation();
  const draft = readDraft(location.state);

  if (!draft) {
    return (
      <EmptyState
        size="page"
        headingLevel={1}
        icon={<Ticket aria-hidden="true" className="h-5 w-5" />}
        title="Choose a fare to book"
        action={
          <div className="flex flex-wrap justify-center gap-2">
            <Link to="/search/flights" className={buttonVariants({ size: 'sm' })}>
              Search flights
            </Link>
            <Link to="/search/buses" className={buttonVariants({ variant: 'outline', size: 'sm' })}>
              Search buses
            </Link>
          </div>
        }
      >
        A booking starts from a search result, so the price and the seats are ones the supplier has
        just offered.
      </EmptyState>
    );
  }

  return <BookingFlow draft={draft} />;
}

function BookingFlow({ draft }: { draft: BookingDraft }) {
  const [step, setStep] = useState<Step>('travellers');
  const [travellers, setTravellers] = useState<TravellerDetails[]>(() =>
    travellerSlots(draft.passengers).map(emptyTraveller),
  );
  const [confirmation, setConfirmation] = useState<PriceConfirmation | null>(null);
  const [changed, setChanged] = useState<PriceConfirmation | null>(null);
  const [declined, setDeclined] = useState(false);
  const [reference, setReference] = useState<string | null>(null);
  const confirm = useConfirmPrice();

  function confirmPrice(details: TravellerDetails[]) {
    setTravellers(details);
    setDeclined(false);
    confirm.mutate(
      { draft, travellers: details },
      {
        onSuccess: (result) => {
          if (priceChanged(result)) {
            setChanged(result);
          } else {
            setConfirmation(result);
            setStep('review');
          }
        },
      },
    );
  }

  const searchPath = draft.product === 'flight' ? '/search/flights' : '/search/buses';

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title={draft.product === 'flight' ? 'Book a flight' : 'Book a bus'}
        description={describeRoute(draft)}
      />

      <Steps current={step} />

      <div className="grid items-start gap-6 lg:grid-cols-3">
        <div className="flex flex-col gap-6 lg:col-span-2">
          {step === 'travellers' ? (
            <>
              {declined ? (
                <Alert
                  tone="warning"
                  title="The new price was not accepted"
                  action={
                    <Link
                      to={searchPath}
                      className={buttonVariants({ size: 'sm', variant: 'outline' })}
                    >
                      Search again
                    </Link>
                  }
                >
                  Nothing was booked or charged. Search again for another fare, or continue to ask
                  the supplier once more.
                </Alert>
              ) : null}
              {confirm.isError ? (
                <ErrorState
                  title="The supplier could not confirm the price"
                  detail={`${describeError(confirm.error).detail} Nothing has been booked or charged.`}
                />
              ) : null}
              <TravellerStep
                draft={draft}
                initial={travellers}
                confirming={confirm.isPending}
                onContinue={confirmPrice}
              />
            </>
          ) : null}

          {step === 'review' && confirmation ? (
            <ReviewStep
              draft={draft}
              travellers={travellers}
              confirmation={confirmation}
              onBack={() => setStep('travellers')}
              onPlaced={(placed) => {
                setReference(placed);
                setStep('ticket');
              }}
            />
          ) : null}

          {step === 'ticket' && reference ? (
            <BookingProgress reference={reference} carrier={carrierOf(draft)} />
          ) : null}
        </div>

        <aside className="lg:sticky lg:top-6">
          <TripSummary draft={draft} confirmation={confirmation} />
        </aside>
      </div>

      <PriceChangeDialog
        confirmation={changed}
        onAccept={() => {
          setConfirmation(changed);
          setChanged(null);
          setStep('review');
        }}
        onDecline={() => {
          setChanged(null);
          setDeclined(true);
        }}
      />
    </div>
  );
}

function Steps({ current }: { current: Step }) {
  const index = STEPS.findIndex((step) => step.id === current);

  return (
    <ol aria-label="Booking steps" className="flex flex-wrap items-center gap-2 text-sm">
      {STEPS.map((step, i) => (
        <li
          key={step.id}
          aria-current={i === index ? 'step' : undefined}
          className={cx(
            'flex items-center gap-2 rounded-full px-3 py-1',
            i === index
              ? 'bg-primary-subtle font-medium text-primary'
              : i < index
                ? 'text-foreground'
                : 'text-muted-foreground',
          )}
        >
          <span className="tabular-nums">{i + 1}</span>
          {step.label}
        </li>
      ))}
    </ol>
  );
}
