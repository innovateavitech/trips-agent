import type { Metadata } from 'next';
import Link from 'next/link';
import { formatMoney, int64 } from '@trips/utils';
import { commerce, type CheckoutStatus } from '../../../lib/api';

/**
 * Where the payment gateway sends the traveller back to.
 *
 * **The browser's return is not evidence of anything.** It says what the payer's browser was told,
 * which is not the gateway answering a question we asked. This page therefore asks the API, which
 * asks the gateway, and reports what the gateway said; the webhook is what guarantees the payment is
 * settled, whether or not the traveller's browser ever came back.
 *
 * A payment still settling is reported as pending rather than as a failure. Telling somebody who has
 * just been charged that it failed invites them to pay twice.
 */

export const metadata: Metadata = {
  title: 'Your payment',
  robots: { index: false, follow: false },
};

interface Props {
  searchParams: Promise<{ reference?: string; payment?: string }>;
}

export default async function CheckoutCompletePage({ searchParams }: Props) {
  const { reference, payment } = await searchParams;

  if (!reference) {
    return <Shell title="We could not find that booking." />;
  }

  const search = payment ? `?payment=${encodeURIComponent(payment)}` : '';

  const { data } = await commerce<CheckoutStatus>(
    `/checkout/${encodeURIComponent(reference)}${search}`,
    { method: 'GET' },
  );

  if (!data) {
    return <Shell title="We could not find that booking." />;
  }

  if (data.status === 'paid') {
    return (
      <Shell title="Thank you — your payment has gone through.">
        <p className="text-sm text-muted-foreground">
          Your booking reference is <strong className="text-foreground">{data.reference}</strong>.
          We are confirming the details now, and we have emailed you a link to manage your booking
          and download your documents.
        </p>

        {data.manageUrl ? (
          <Link
            href={data.manageUrl}
            className="mt-6 inline-flex h-11 items-center justify-center rounded-md bg-primary px-6 text-sm font-medium text-primary-foreground transition-colors hover:bg-primary-hover focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2"
          >
            See your booking
          </Link>
        ) : null}
      </Shell>
    );
  }

  if (data.status === 'failed') {
    return (
      <Shell title="That payment did not go through.">
        <p className="text-sm text-muted-foreground">
          Nothing has been charged. Your cart is still here — try again, or get in touch and we will
          take it from there.
        </p>
        <Link
          href="/checkout"
          className="mt-6 inline-flex h-11 items-center justify-center rounded-md bg-primary px-6 text-sm font-medium text-primary-foreground transition-colors hover:bg-primary-hover focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2"
        >
          Try again
        </Link>
      </Shell>
    );
  }

  return (
    <Shell title="We are still waiting for your payment to clear.">
      <p className="text-sm text-muted-foreground">
        Your booking reference is <strong className="text-foreground">{data.reference}</strong>, for{' '}
        {formatMoney(int64(data.amountDueMinor), data.currency)}. Some payments take a few minutes
        to settle. Refresh this page shortly — and if you have been charged, you will not be charged
        again.
      </p>
    </Shell>
  );
}

function Shell({ title, children }: { title: string; children?: React.ReactNode }) {
  return (
    <div className="mx-auto max-w-2xl px-4 py-16 sm:px-6">
      <h1 className="text-3xl font-semibold tracking-tight text-foreground">{title}</h1>
      <div className="mt-4">{children}</div>
    </div>
  );
}
