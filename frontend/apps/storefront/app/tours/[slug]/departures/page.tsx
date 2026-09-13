import type { Metadata } from 'next';
import Link from 'next/link';
import { notFound } from 'next/navigation';
import { formatMoney, int64 } from '@trips/utils';
import { getDepartures, getProduct, getSite, type Departure } from '../../../../lib/api';
import { AddToCartForm } from '../../../../components/add-to-cart-form';

/**
 * The dated departures on one trip: when they go, what is left, what a party pays, and what is due
 * today (build plan F6, sold through F5).
 *
 * **A party size changes the price**, because a departure's price bands are per head and depend on
 * how many people are going. The size lives in the URL, so a traveller can send somebody the page
 * they were actually looking at, and so the page works with JavaScript switched off.
 *
 * Every figure is the sell price. The agent's own seat price, their markup and the platform's fee
 * are not on this page and never will be (CLAUDE.md rule 4).
 */

interface Props {
  params: Promise<{ slug: string }>;
  searchParams: Promise<{ adults?: string; children?: string; infants?: string }>;
}

export async function generateMetadata({ params }: Props): Promise<Metadata> {
  const [{ slug }, site] = await Promise.all([params, getSite()]);

  if (!site) {
    return {};
  }

  const product = await getProduct(site.siteId, slug);

  return product
    ? {
        title: `Dates for ${product.title}`,
        description: `Departure dates and prices for ${product.title}.`,
        alternates: { canonical: `/tours/${product.slug}/departures` },
      }
    : {};
}

export default async function DeparturesPage({ params, searchParams }: Props) {
  const [{ slug }, query, site] = await Promise.all([params, searchParams, getSite()]);

  if (!site) {
    notFound();
  }

  const product = await getProduct(site.siteId, slug);

  if (!product) {
    notFound();
  }

  const party = {
    adults: whole(query.adults, 1, 1),
    children: whole(query.children, 0, 0),
    infants: whole(query.infants, 0, 0),
  };

  const departures = await getDepartures(slug, party);

  return (
    <div className="mx-auto max-w-4xl px-4 py-12 sm:px-6">
      <header className="mb-8">
        <Link
          href={`/tours/${product.slug}`}
          className="text-sm font-medium text-muted-foreground underline underline-offset-4 transition-colors hover:text-foreground"
        >
          &larr; {product.title}
        </Link>

        <h1 className="mt-3 text-3xl font-semibold tracking-tight text-foreground sm:text-4xl">
          Departure dates
        </h1>
        <p className="mt-3 text-sm text-muted-foreground">
          Prices are for the whole party and include everything listed on the trip page.
        </p>
      </header>

      <PartyPicker slug={slug} party={party} />

      {departures.length === 0 ? (
        <p className="mt-10 rounded-lg border border-border bg-card p-8 text-center text-sm text-muted-foreground">
          There are no dates on sale for a party this size just now. Get in touch and we will look
          at what else we can do.
        </p>
      ) : (
        <ul className="mt-10 space-y-6">
          {departures.map((departure) => (
            <DepartureCard key={departure.id} departure={departure} party={party} />
          ))}
        </ul>
      )}
    </div>
  );
}

function DepartureCard({
  departure,
  party,
}: {
  departure: Departure;
  party: { adults: number; children: number; infants: number };
}) {
  const seatsLeft = int64(departure.seatsLeft);
  const sellable = departure.status !== 'SoldOut' && departure.status !== 'Closed' && seatsLeft > 0;
  const deposit = int64(departure.dueNowMinor) < int64(departure.totalMinor);

  return (
    <li className="rounded-lg border border-border bg-card p-6">
      <div className="flex flex-wrap items-start justify-between gap-x-8 gap-y-4">
        <div className="min-w-0">
          <p className="text-lg font-semibold text-foreground">
            {readableDate(departure.departureDate)}
          </p>
          <p className="mt-1 text-sm">
            <StatusBadge status={departure.status} seatsLeft={seatsLeft} />
            {departure.isGroupDeparture && int64(departure.minPax) > 0 ? (
              <span className="ml-3 text-muted-foreground">
                Runs with {int64(departure.minPax)} or more
              </span>
            ) : null}
          </p>
          <p className="mt-2 text-xs text-muted-foreground">
            Book by {readableInstant(departure.bookByAt)}
          </p>
        </div>

        <div className="text-right">
          <p className="text-2xl font-semibold text-foreground">
            {formatMoney(int64(departure.totalMinor), departure.currency)}
          </p>
          <p className="text-xs text-muted-foreground">
            {formatMoney(int64(departure.pricePerPaxMinor), departure.currency)} each
          </p>
        </div>
      </div>

      {departure.payments.length > 1 && (
        <table className="mt-6 w-full text-left text-sm">
          <caption className="mb-2 text-left text-xs uppercase tracking-wide text-muted-foreground">
            How you pay
          </caption>
          <tbody>
            {departure.payments.map((payment) => (
              <tr key={payment.sequence} className="border-b border-border last:border-0">
                <td className="py-2 pr-4 text-foreground">{payment.label}</td>
                <td className="py-2 pr-4 text-muted-foreground">{readableDate(payment.dueDate)}</td>
                <td className="py-2 text-right font-medium text-foreground">
                  {formatMoney(int64(payment.amountMinor), departure.currency)}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      {sellable ? (
        <div className="mt-6">
          <AddToCartForm
            departureId={departure.id}
            party={party}
            label={
              deposit
                ? `Book with ${formatMoney(int64(departure.dueNowMinor), departure.currency)} today`
                : 'Add to cart'
            }
          />
          {deposit ? (
            <p className="mt-2 text-xs text-muted-foreground">
              The rest is due on the dates above. We will remind you before each one.
            </p>
          ) : null}
        </div>
      ) : (
        <p className="mt-6 text-sm text-muted-foreground">
          This date is not available. Ask us about the others, or join the waiting list.
        </p>
      )}
    </li>
  );
}

/** Who is going. A plain form, so it works before any JavaScript has loaded. */
function PartyPicker({
  slug,
  party,
}: {
  slug: string;
  party: { adults: number; children: number; infants: number };
}) {
  return (
    <form
      action={`/tours/${slug}/departures`}
      method="get"
      className="flex flex-wrap items-end gap-4 rounded-lg border border-border bg-card p-4"
    >
      <Counter label="Adults" name="adults" value={party.adults} min={1} />
      <Counter label="Children" name="children" value={party.children} min={0} />
      <Counter label="Infants" name="infants" value={party.infants} min={0} />

      <button
        type="submit"
        className="inline-flex h-10 items-center justify-center rounded-md border border-input bg-background px-4 text-sm font-medium text-foreground transition-colors hover:bg-muted focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2"
      >
        Update prices
      </button>
    </form>
  );
}

function Counter({
  label,
  name,
  value,
  min,
}: {
  label: string;
  name: string;
  value: number;
  min: number;
}) {
  return (
    <label className="flex flex-col gap-1.5">
      <span className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
        {label}
      </span>
      <input
        type="number"
        name={name}
        defaultValue={value}
        min={min}
        max={20}
        inputMode="numeric"
        className="h-10 w-20 rounded-md border border-input bg-background px-3 text-sm text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
      />
    </label>
  );
}

function StatusBadge({ status, seatsLeft }: { status: string; seatsLeft: number }) {
  const [text, tone] =
    status === 'SoldOut'
      ? ['Sold out', 'bg-muted text-muted-foreground']
      : status === 'Closed'
        ? ['Closed', 'bg-muted text-muted-foreground']
        : status === 'NearlyFull'
          ? [`Only ${seatsLeft} left`, 'bg-warning-subtle text-warning-subtle-foreground']
          : status === 'Guaranteed'
            ? ['Guaranteed to run', 'bg-success-subtle text-success-subtle-foreground']
            : [`${seatsLeft} seats left`, 'bg-muted text-muted-foreground'];

  return (
    <span className={`inline-flex rounded-full px-2.5 py-0.5 text-xs font-medium ${tone}`}>
      {text}
    </span>
  );
}

/** "12 March 2027", from the YYYY-MM-DD the API sends. */
function readableDate(date: string): string {
  const parsed = new Date(`${date}T00:00:00Z`);

  return Number.isNaN(parsed.getTime())
    ? date
    : parsed.toLocaleDateString('en-GB', {
        day: 'numeric',
        month: 'long',
        year: 'numeric',
        timeZone: 'UTC',
      });
}

function readableInstant(instant: string): string {
  const parsed = new Date(instant);

  return Number.isNaN(parsed.getTime())
    ? instant
    : parsed.toLocaleDateString('en-GB', { day: 'numeric', month: 'long', year: 'numeric' });
}

function whole(value: string | undefined, fallback: number, min: number): number {
  const parsed = Number(value);

  return Number.isSafeInteger(parsed) && parsed >= min && parsed <= 20 ? parsed : fallback;
}
