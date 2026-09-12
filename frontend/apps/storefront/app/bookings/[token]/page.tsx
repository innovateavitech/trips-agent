import type { Metadata } from 'next';
import { notFound } from 'next/navigation';
import { formatMoney, int64 } from '@trips/utils';
import { getBooking, type Booking } from '../../../lib/api';

/**
 * "Manage my booking", behind the link in the traveller's email (build plan F5, decision 21).
 *
 * There is no account and no password: the link is the credential, which is why the page is never
 * indexed and never cached. What it shows is the traveller's own booking — what they bought, where
 * each item has got to, what they have paid, what is still to pay, and their documents.
 *
 * **It is honest about failure.** An item waiting in the agency's resolution queue says so, in plain
 * words, with what is being done about it. A page that quietly showed it as confirmed would be the
 * worst thing this feature could do.
 */

export const metadata: Metadata = {
  title: 'Your booking',
  robots: { index: false, follow: false },
};

interface Props {
  params: Promise<{ token: string }>;
}

export default async function BookingPage({ params }: Props) {
  const { token } = await params;
  const booking = await getBooking(token);

  if (!booking) {
    notFound();
  }

  const outstanding = int64(booking.totalMinor) - int64(booking.paidMinor);

  return (
    <div className="mx-auto max-w-3xl px-4 py-12 sm:px-6">
      <header className="mb-8">
        <p className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
          Booking {booking.reference}
        </p>
        <h1 className="mt-2 text-3xl font-semibold tracking-tight text-foreground">
          {headline(booking.status)}
        </h1>
        <p className="mt-3 text-sm text-muted-foreground">{explanation(booking)}</p>
      </header>

      <section className="mb-10">
        <h2 className="mb-4 text-xl font-semibold tracking-tight text-foreground">
          What you booked
        </h2>

        <ul className="divide-y divide-border border-y border-border">
          {booking.lines.map((line) => (
            <li key={line.id} className="py-5">
              <div className="flex flex-wrap items-baseline justify-between gap-x-6 gap-y-1">
                <p className="text-base font-medium text-foreground">{line.title}</p>
                <p className="text-base font-semibold text-foreground">
                  {formatMoney(int64(line.priceMinor), booking.currency)}
                </p>
              </div>

              <p className="mt-1 text-sm">
                <StatusBadge status={line.status} />
                {line.reference ? (
                  <span className="ml-3 text-muted-foreground">
                    Reference <span className="font-medium text-foreground">{line.reference}</span>
                  </span>
                ) : null}
              </p>

              {line.statusDetail ? (
                <p className="mt-2 text-sm text-muted-foreground">{line.statusDetail}</p>
              ) : null}
            </li>
          ))}
        </ul>
      </section>

      <section className="mb-10">
        <h2 className="mb-4 text-xl font-semibold tracking-tight text-foreground">Payments</h2>

        <dl className="grid gap-3 sm:grid-cols-3">
          <Fact
            label="Booking total"
            value={formatMoney(int64(booking.totalMinor), booking.currency)}
          />
          <Fact label="Paid" value={formatMoney(int64(booking.paidMinor), booking.currency)} />
          <Fact
            label="Still to pay"
            value={formatMoney(outstanding > 0 ? outstanding : 0, booking.currency)}
          />
        </dl>

        {booking.instalments.length > 0 && (
          <table className="mt-6 w-full text-left text-sm">
            <thead>
              <tr className="border-b border-border text-xs uppercase tracking-wide text-muted-foreground">
                <th scope="col" className="py-2 pr-4 font-medium">
                  Payment
                </th>
                <th scope="col" className="py-2 pr-4 font-medium">
                  Due
                </th>
                <th scope="col" className="py-2 pr-4 font-medium">
                  Status
                </th>
                <th scope="col" className="py-2 text-right font-medium">
                  Amount
                </th>
              </tr>
            </thead>
            <tbody>
              {booking.instalments.map((instalment) => (
                <tr key={instalment.sequence} className="border-b border-border last:border-0">
                  <td className="py-3 pr-4 text-foreground">{instalment.label}</td>
                  <td className="py-3 pr-4 text-muted-foreground">{instalment.dueDate}</td>
                  <td className="py-3 pr-4 text-muted-foreground">{instalment.status}</td>
                  <td className="py-3 text-right font-medium text-foreground">
                    {formatMoney(int64(instalment.amountMinor), booking.currency)}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>

      {booking.documents.length > 0 && (
        <section className="mb-10">
          <h2 className="mb-4 text-xl font-semibold tracking-tight text-foreground">
            Your documents
          </h2>
          <ul className="space-y-2 text-sm">
            {booking.documents.map((document) => (
              <li key={document.id}>
                <a
                  href={document.downloadUrl}
                  className="font-medium text-primary underline underline-offset-4 hover:text-primary-hover"
                >
                  {document.kind} {document.number}
                </a>
              </li>
            ))}
          </ul>
        </section>
      )}

      <section className="rounded-lg border border-border bg-card p-6">
        <h2 className="text-base font-semibold text-foreground">Need a hand?</h2>
        <p className="mt-2 text-sm text-muted-foreground">
          Get in touch with {booking.agencyName} and we will sort it out.
        </p>
        <p className="mt-3 text-sm">
          {booking.agencyEmail ? (
            <a
              href={`mailto:${booking.agencyEmail}`}
              className="font-medium text-primary underline underline-offset-4 hover:text-primary-hover"
            >
              {booking.agencyEmail}
            </a>
          ) : null}
          {booking.agencyPhone ? (
            <span className="ml-4 text-foreground">{booking.agencyPhone}</span>
          ) : null}
        </p>
      </section>
    </div>
  );
}

/** The one line at the top of the page, in the traveller's words. */
function headline(status: string): string {
  switch (status) {
    case 'Confirmed':
      return 'Your booking is confirmed.';
    case 'AwaitingPayment':
      return 'Your booking is waiting for payment.';
    case 'NeedsAttention':
      return 'One item on your booking needs attention.';
    case 'Cancelled':
      return 'This booking was cancelled.';
    case 'Refunded':
      return 'This booking has been refunded.';
    default:
      return 'We are confirming your booking.';
  }
}

function explanation(booking: Booking): string {
  switch (booking.status) {
    case 'NeedsAttention':
      return `${booking.agencyName} is sorting it out and will be in touch. Everything else on this booking stands.`;
    case 'AwaitingPayment':
      return 'Nothing has been charged yet.';
    case 'Confirmed':
      return 'Everything is booked. Your documents are below.';
    default:
      return 'Some items are still being confirmed with the operator. We will email you as each one is.';
  }
}

function StatusBadge({ status }: { status: string }) {
  const tone =
    status === 'Confirmed'
      ? 'bg-success-subtle text-success-subtle-foreground'
      : status === 'NeedsAttention'
        ? 'bg-warning-subtle text-warning-subtle-foreground'
        : status === 'Refunded' || status === 'Cancelled'
          ? 'bg-muted text-muted-foreground'
          : 'bg-muted text-muted-foreground';

  return (
    <span className={`inline-flex rounded-full px-2.5 py-0.5 text-xs font-medium ${tone}`}>
      {status === 'NeedsAttention' ? 'Needs attention' : status}
    </span>
  );
}

function Fact({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-md border border-border bg-card px-4 py-3">
      <dt className="text-xs uppercase tracking-wide text-muted-foreground">{label}</dt>
      <dd className="mt-1 text-base font-semibold text-foreground">{value}</dd>
    </div>
  );
}
