import { useEffect, useRef } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { Alert, Card, LoadingState, buttonVariants } from '@trips/ui';
import { formatMoney } from '@trips/utils';
import { PageHeader } from '../../../shell/page-header';
import { useCompleteCheckout } from '../billing-queries';

/**
 * Where Paystack sends the agency back to after paying.
 *
 * Nothing here decides anything. It hands the gateway's reference to the server, which asks the
 * gateway what actually happened — a browser redirect says what the payer's browser was told, and
 * that is not the same thing.
 *
 * If the answer is not ready yet the screen says so rather than saying the payment failed. Telling
 * somebody a payment failed when their card was in fact debited is how they end up paying twice.
 */
export function BillingReturnPage() {
  const [params] = useSearchParams();
  const reference = params.get('reference') ?? params.get('trxref');

  const complete = useCompleteCheckout();
  const asked = useRef(false);

  useEffect(() => {
    // Once. React's development mode mounts effects twice, and a second ask is harmless but
    // pointless — the server has already given its answer.
    if (reference === null || asked.current) return;

    asked.current = true;
    complete.mutate(reference);
  }, [reference, complete]);

  return (
    <div className="flex flex-col gap-6">
      <PageHeader title="Finishing your payment" />

      <Card className="p-6">
        {reference === null ? (
          <Alert tone="warning" title="We do not know which payment this was">
            The link is missing its reference. Open Billing and check your invoices — if the payment
            went through, it is there.
          </Alert>
        ) : null}

        {complete.isPending ? (
          <LoadingState size="page" label="Checking with the payment provider" />
        ) : null}

        {complete.isError ? (
          <Alert tone="warning" title="We have not got an answer yet">
            Your card may well have been charged. Do not pay again — open Billing in a minute and
            your invoice will say.
          </Alert>
        ) : null}

        {complete.data?.result === 'applied' ? (
          <Alert tone="success" title={`You are on the ${complete.data.plan.planName} plan`}>
            {complete.data.plan.amountMinor === null
              ? 'Nothing further to pay.'
              : `${formatMoney(complete.data.plan.amountMinor, complete.data.plan.currency)} a month. ` +
                'Your receipt is with your invoices.'}
          </Alert>
        ) : null}

        {complete.data?.result === 'scheduled' ? (
          <Alert tone="info" title="Your change is scheduled">
            {complete.data.change.explanation}
          </Alert>
        ) : null}

        <div className="pt-4">
          <Link to="/billing" className={buttonVariants({ variant: 'primary' })}>
            Back to billing
          </Link>
        </div>
      </Card>
    </div>
  );
}
