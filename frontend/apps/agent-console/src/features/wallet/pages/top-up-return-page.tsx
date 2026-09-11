import { useEffect } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import {
  Alert,
  Card,
  CardContent,
  CardHeader,
  CardTitle,
  Skeleton,
  buttonVariants,
} from '@trips/ui';
import { formatMoney } from '@trips/utils';
import { clearPendingTopUp, readPendingTopUp } from '../pending-top-up';
import { useRefreshWallet, useTopUpResult } from '../wallet-queries';

/**
 * Where Paystack sends the agent back to.
 *
 * The single most important thing about this page: **it does not credit
 * anything.** It reads the reference out of the URL and asks our server what
 * happened. The redirect itself proves nothing — the agent can edit the
 * address bar, the URL can be replayed, and it routinely arrives before
 * Paystack's webhook does. The server verifies with Paystack and writes the
 * ledger entries (issue #26); this page reports the verdict.
 *
 * It handles all four outcomes the AC asks for: success, failure, abandonment,
 * and the awkward fifth one — still pending after we have waited a while.
 */
export function TopUpReturnPage() {
  const [params] = useSearchParams();
  const refreshWallet = useRefreshWallet();

  // Paystack appends both `reference` and `trxref`; they carry the same value.
  // The breadcrumb is the fallback for a return URL that lost its query string.
  const reference =
    params.get('reference') ?? params.get('trxref') ?? readPendingTopUp()?.reference ?? null;

  const result = useTopUpResult(reference);
  const status = result.data?.status;

  useEffect(() => {
    if (!status || status === 'pending') return;

    // Settled one way or the other: the breadcrumb has done its job.
    clearPendingTopUp();

    if (status === 'succeeded') {
      // Refetch the balance and the statement rather than adding the amount to
      // whatever we had cached — see useRefreshWallet for why that matters.
      void refreshWallet();
    }
  }, [status, refreshWallet]);

  return (
    <div className="flex w-full max-w-xl flex-col gap-6">
      <Card>
        <CardHeader>
          <CardTitle>Top-up</CardTitle>
        </CardHeader>
        <CardContent className="flex flex-col gap-4">
          <Outcome reference={reference} result={result} />

          <div className="flex gap-2">
            <Link to="/wallet" className={buttonVariants({ variant: 'outline', size: 'sm' })}>
              Back to wallet
            </Link>
          </div>
        </CardContent>
      </Card>
    </div>
  );
}

function Outcome({
  reference,
  result,
}: {
  reference: string | null;
  result: ReturnType<typeof useTopUpResult>;
}) {
  if (!reference) {
    return (
      <Alert tone="warning" title="We could not identify that payment">
        There is no payment reference in this link. If you completed a payment, open your wallet — a
        confirmed top-up appears on your statement automatically.
      </Alert>
    );
  }

  if (result.isError) {
    return (
      <Alert tone="warning" title="We could not check this payment just now">
        This does not mean it failed. Your balance and statement are the record — open your wallet
        in a moment to see whether it arrived.{' '}
        <strong className="font-medium">Please do not pay again.</strong>
      </Alert>
    );
  }

  if (result.isPending) {
    return (
      <div aria-busy="true" aria-label="Checking your payment" className="flex flex-col gap-2">
        <Skeleton className="h-5 w-48" />
        <Skeleton className="h-4 w-full" />
        <Skeleton className="h-4 w-2/3" />
      </div>
    );
  }

  const { status, amountMinor, currency, failureReason } = result.data;

  switch (status) {
    case 'succeeded':
      return (
        <Alert tone="success" title="Your wallet has been topped up">
          {formatMoney(amountMinor, currency)} has been added and is available to spend now. A
          receipt is on its way to your email.
        </Alert>
      );

    case 'failed':
      return (
        <Alert tone="destructive" title="That payment did not go through">
          {failureReason ?? 'The payment was not completed.'} Your wallet balance is unchanged, and
          you have not been charged. You can try again from your wallet.
        </Alert>
      );

    case 'abandoned':
      return (
        <Alert tone="warning" title="You left before the payment was finished">
          Nothing has been charged and your balance is unchanged. You can start a new top-up from
          your wallet whenever you are ready.
        </Alert>
      );

    case 'pending':
    default:
      return (
        // The honest state, and the one worth getting right. Telling someone to
        // "try again" here is how a wallet gets credited twice.
        <Alert tone="info" title="We are still waiting for confirmation">
          Your bank has not confirmed this payment to us yet. This page keeps checking, and your
          balance updates by itself the moment it clears — usually within a minute.{' '}
          <strong className="font-medium">Please do not pay again.</strong>
        </Alert>
      );
  }
}
