import type { Metadata } from 'next';
import { notFound } from 'next/navigation';
import { formatMoney } from '@trips/utils';
import { int64 } from '@trips/utils';
import { getQuote, getSite, type Quote } from '../../../lib/api';
import { PlainText } from '../../../components/plain-text';
import { QuoteActions } from './quote-actions';

/**
 * One customer's quote, on the agency's own address (issue 62).
 *
 * The token in the link is the whole of the authentication: there are no traveller accounts (MVP
 * decision 21), so the link is the capability. That is why the page is never indexed and never
 * cached — a shared cache keyed on the URL would be keyed on the secret.
 *
 * Opening it is what tells the agency their customer has read it, which the API records on the first
 * read.
 */

export const metadata: Metadata = {
  title: 'Your quote',
  // Never indexed. The URL contains the token, so a crawler that found one would be handing a
  // stranger somebody's quote.
  robots: { index: false, follow: false, nocache: true },
};

export default async function QuotePage({ params }: { params: Promise<{ token: string }> }) {
  const [{ token }, site] = await Promise.all([params, getSite()]);

  if (!site) {
    notFound();
  }

  const quote = await getQuote(token);

  if (!quote) {
    return (
      <section className="mx-auto max-w-2xl px-4 py-24 text-center sm:px-6">
        <h1 className="text-2xl font-semibold tracking-tight text-foreground">
          We could not find that quote
        </h1>
        <p className="mt-3 text-base text-muted-foreground">
          The link may have expired, or it may have been copied incompletely. Please reply to our
          email and we will send it again.
        </p>
      </section>
    );
  }

  const total = int64(quote.totalMinor);

  return (
    <article className="mx-auto max-w-3xl px-4 py-12 sm:px-6">
      <header>
        <p className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
          Quote {quote.quoteNumber}
        </p>
        <h1 className="mt-1 text-3xl font-semibold tracking-tight text-foreground">
          {quote.title}
        </h1>
        <p className="mt-2 text-base text-muted-foreground">
          Prepared for {quote.customerName}. Valid until{' '}
          {new Date(quote.validUntil).toLocaleDateString()}.
        </p>

        <Status quote={quote} />
      </header>

      {quote.itinerary.length > 0 ? (
        <section className="mt-10">
          <h2 className="mb-4 text-xl font-semibold tracking-tight text-foreground">Your trip</h2>
          <ol className="space-y-6">
            {quote.itinerary.map((day) => (
              <li key={day.dayNumber} className="border-l-2 border-primary-border pl-4">
                <h3 className="text-base font-semibold text-foreground">
                  Day {day.dayNumber}
                  {day.title ? ` · ${day.title}` : ''}
                </h3>
                {day.description ? (
                  <p className="mt-1 whitespace-pre-line text-sm text-muted-foreground">
                    {day.description}
                  </p>
                ) : null}
              </li>
            ))}
          </ol>
        </section>
      ) : null}

      <section className="mt-10">
        <h2 className="mb-4 text-xl font-semibold tracking-tight text-foreground">
          What it covers
        </h2>

        <div className="overflow-x-auto">
          <table className="w-full text-left text-sm">
            <thead>
              <tr className="border-b border-border text-xs uppercase tracking-wide text-muted-foreground">
                <th scope="col" className="py-2 pr-4 font-medium">
                  Item
                </th>
                <th scope="col" className="py-2 pr-4 text-right font-medium">
                  Quantity
                </th>
                <th scope="col" className="py-2 text-right font-medium">
                  Price
                </th>
              </tr>
            </thead>
            <tbody>
              {quote.items.map((item) => (
                <tr key={item.description} className="border-b border-border">
                  <td className="py-3 pr-4 text-foreground">{item.description}</td>
                  <td className="py-3 pr-4 text-right text-muted-foreground">{item.quantity}</td>
                  <td className="py-3 text-right text-foreground">
                    {formatMoney(int64(item.unitPriceMinor) * int64(item.quantity), quote.currency)}
                  </td>
                </tr>
              ))}
            </tbody>
            <tfoot>
              <tr>
                <th
                  scope="row"
                  colSpan={2}
                  className="py-3 pr-4 text-right font-semibold text-foreground"
                >
                  Total
                </th>
                <td className="py-3 text-right text-lg font-semibold text-foreground">
                  {formatMoney(total, quote.currency)}
                </td>
              </tr>
            </tfoot>
          </table>
        </div>
      </section>

      {quote.notes ? (
        <section className="mt-10">
          <h2 className="mb-3 text-xl font-semibold tracking-tight text-foreground">Notes</h2>
          {/* Plain text, as the agent typed it — never markup. See components/plain-text. */}
          <PlainText text={quote.notes} className="space-y-3" />
        </section>
      ) : null}

      {quote.canRespond ? <QuoteActions token={token} /> : null}
    </article>
  );
}

/** Where the quote stands, said plainly, because it changes what the page is for. */
function Status({ quote }: { quote: Quote }) {
  if (quote.canRespond) {
    return null;
  }

  const message =
    quote.status === 'Accepted'
      ? 'You accepted this quote. We will be in touch about the next steps.'
      : quote.status === 'Declined'
        ? 'You let us know this one was not for you. If anything changes, just reply to our email.'
        : 'This quote has passed its date. Reply to our email and we will put together a fresh one.';

  return <p className="mt-4 rounded-md bg-muted px-4 py-3 text-sm text-foreground">{message}</p>;
}
