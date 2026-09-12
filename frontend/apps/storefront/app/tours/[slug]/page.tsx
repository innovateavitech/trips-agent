import type { Metadata } from 'next';
import Link from 'next/link';
import { notFound } from 'next/navigation';
import { formatMoney, formatMoneyShort, int64 } from '@trips/utils';
import { getProduct, getSite, type Product } from '../../../lib/api';
import { PlainText } from '../../../components/plain-text';

/**
 * One product's own page: what it is, what it costs, and how to ask for it.
 *
 * Every figure on this page is the sell price the traveller pays. The net rate behind it and the
 * agency's markup never leave the console (CLAUDE.md rules 4 and 5).
 */

interface Props {
  params: Promise<{ slug: string }>;
}

export async function generateMetadata({ params }: Props): Promise<Metadata> {
  const [{ slug }, site] = await Promise.all([params, getSite()]);

  if (!site) {
    return {};
  }

  const product = await getProduct(site.siteId, slug);

  if (!product) {
    return {};
  }

  return {
    title: product.title,
    // The agent's own summary, which is written for travellers. Trimmed rather than padded: a
    // description a search engine truncates mid-word reads worse than a short one.
    description: product.summary.slice(0, 300) || undefined,
    alternates: { canonical: `/tours/${product.slug}` },
    openGraph: {
      title: product.title,
      description: product.summary.slice(0, 300) || undefined,
      images: product.images.length > 0 ? [{ url: product.images[0]!.url }] : undefined,
      type: 'website',
    },
  };
}

export default async function ProductPage({ params }: Props) {
  const [{ slug }, site] = await Promise.all([params, getSite()]);

  if (!site) {
    notFound();
  }

  const product = await getProduct(site.siteId, slug);

  if (!product) {
    notFound();
  }

  const cover = product.images[0];
  const place = [product.destinationCity, product.destinationCountry].filter(Boolean).join(', ');

  return (
    <article className="mx-auto max-w-4xl px-4 py-12 sm:px-6">
      {/*
        The structured data search engines read. Built from the same fields the page shows, so the two
        can never disagree — the rule Google enforces and the honest thing to do either way.
      */}
      <script
        type="application/ld+json"
        // The value is JSON we serialise ourselves from typed fields, never markup from a person.
        dangerouslySetInnerHTML={{
          __html: JSON.stringify(structuredData(product, site.content.site.name)),
        }}
      />

      <header className="mb-8">
        <p className="mb-2 text-xs font-medium uppercase tracking-wide text-muted-foreground">
          {product.productType}
          {place && <span className="normal-case tracking-normal"> &middot; {place}</span>}
        </p>

        <h1 className="text-3xl font-semibold tracking-tight text-foreground sm:text-4xl">
          {product.title}
        </h1>

        {product.summary && <p className="mt-3 text-lg text-muted-foreground">{product.summary}</p>}

        <p className="mt-6 flex items-baseline gap-2">
          <span className="text-sm text-muted-foreground">from</span>
          <span className="text-3xl font-semibold text-foreground">
            {formatMoney(int64(product.fromPriceMinor), product.currency)}
          </span>
          {product.durationDays ? (
            <span className="text-sm text-muted-foreground">
              &middot; {product.durationDays} {int64(product.durationDays) === 1 ? 'day' : 'days'}
            </span>
          ) : null}
        </p>
      </header>

      {cover && (
        <figure className="mb-10 overflow-hidden rounded-lg bg-muted">
          {/* eslint-disable-next-line @next/next/no-img-element */}
          <img src={cover.url} alt={cover.caption} className="w-full object-cover" />
          {cover.caption && (
            <figcaption className="px-4 py-2 text-xs text-muted-foreground">
              {cover.caption}
            </figcaption>
          )}
        </figure>
      )}

      {product.description && (
        <Section title="About this trip">
          {/* Plain text, as the agent typed it. Never HTML — see components/plain-text. */}
          <PlainText text={product.description} className="space-y-4" />
        </Section>
      )}

      {product.itinerary.length > 0 && (
        <Section title="Day by day">
          <ol className="space-y-6">
            {product.itinerary.map((day) => (
              <li key={day.dayNumber} className="border-l-2 border-primary-border pl-4">
                <h3 className="text-base font-semibold text-foreground">
                  Day {day.dayNumber}
                  {day.title && ` · ${day.title}`}
                </h3>
                {day.description && (
                  <p className="mt-1 whitespace-pre-line text-sm text-muted-foreground">
                    {day.description}
                  </p>
                )}
                {(day.meals.length > 0 || day.accommodation) && (
                  <p className="mt-2 text-xs text-muted-foreground">
                    {day.meals.length > 0 && <>Meals: {day.meals.join(', ')}. </>}
                    {day.accommodation && <>Staying at {day.accommodation}.</>}
                  </p>
                )}
              </li>
            ))}
          </ol>
        </Section>
      )}

      {product.inclusions.length > 0 && (
        <Section title="What is included">
          <div className="grid gap-8 sm:grid-cols-2">
            <InclusionList
              heading="Included"
              items={product.inclusions.filter((item) => item.kind === 'Inclusion')}
            />
            <InclusionList
              heading="Not included"
              items={product.inclusions.filter((item) => item.kind === 'Exclusion')}
            />
          </div>
        </Section>
      )}

      {product.prices.length > 0 && (
        <Section title="Prices">
          <table className="w-full text-left text-sm">
            <thead>
              <tr className="border-b border-border text-xs uppercase tracking-wide text-muted-foreground">
                <th scope="col" className="py-2 pr-4 font-medium">
                  Option
                </th>
                <th scope="col" className="py-2 pr-4 font-medium">
                  Traveller
                </th>
                <th scope="col" className="py-2 text-right font-medium">
                  Price
                </th>
              </tr>
            </thead>
            <tbody>
              {product.prices.map((price) => (
                <tr
                  key={`${price.name}-${price.paxType}`}
                  className="border-b border-border last:border-0"
                >
                  <td className="py-3 pr-4 text-foreground">{price.name}</td>
                  <td className="py-3 pr-4 text-muted-foreground">
                    {price.paxType}
                    {price.occupancy ? ` · ${price.occupancy} sharing` : ''}
                  </td>
                  <td className="py-3 text-right font-medium text-foreground">
                    {formatMoneyShort(int64(price.priceMinor), product.currency)}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </Section>
      )}

      {product.visa && (
        <Section title="About this visa">
          <dl className="mb-6 grid gap-4 sm:grid-cols-2">
            <Fact label="Visa type" value={product.visa.visaType} />
            <Fact label="Entries" value={product.visa.entryType} />
            <Fact label="Processing time" value={`${product.visa.processingTimeDays} days`} />
            <Fact label="Valid for" value={`${product.visa.validityDays} days`} />
            <Fact
              label="Total fee"
              value={formatMoney(int64(product.visa.totalFeeMinor), product.currency)}
            />
          </dl>

          {product.visa.documents.length > 0 && (
            <>
              <h3 className="mb-2 text-base font-semibold text-foreground">What you will need</h3>
              <ul className="space-y-1.5 text-sm">
                {product.visa.documents.map((document) => (
                  <li key={document.label} className="text-foreground">
                    {document.label}
                    {!document.isMandatory && (
                      <span className="text-muted-foreground"> (if you have it)</span>
                    )}
                  </li>
                ))}
              </ul>
            </>
          )}
        </Section>
      )}

      <Section title="Interested?">
        <p className="text-sm text-muted-foreground">
          Get in touch and we will hold your place and answer any questions.
        </p>
        <Link
          href="/contact"
          className="mt-4 inline-flex items-center justify-center rounded-md bg-primary px-6 py-3 text-sm font-medium text-primary-foreground transition-colors hover:bg-primary-hover focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2"
        >
          Enquire about this trip
        </Link>
      </Section>
    </article>
  );
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <section className="mb-10">
      <h2 className="mb-4 text-xl font-semibold tracking-tight text-foreground">{title}</h2>
      {children}
    </section>
  );
}

function InclusionList({
  heading,
  items,
}: {
  heading: string;
  items: readonly { text: string }[];
}) {
  if (items.length === 0) {
    return null;
  }

  return (
    <div>
      <h3 className="mb-2 text-sm font-semibold text-foreground">{heading}</h3>
      <ul className="space-y-1.5 text-sm text-muted-foreground">
        {items.map((item) => (
          <li key={item.text}>{item.text}</li>
        ))}
      </ul>
    </div>
  );
}

function Fact({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <dt className="text-xs font-medium uppercase tracking-wide text-muted-foreground">{label}</dt>
      <dd className="mt-0.5 text-sm text-foreground">{value}</dd>
    </div>
  );
}

/**
 * The product as schema.org describes it, so a search engine can show the price and the offer rather
 * than guessing at them. The seller is the agency, and only the agency.
 */
function structuredData(product: Product, sellerName: string) {
  return {
    '@context': 'https://schema.org',
    '@type': 'Product',
    name: product.title,
    description: product.summary || product.description.slice(0, 500),
    image: product.images.map((image) => image.url),
    brand: { '@type': 'Organization', name: sellerName },
    offers: {
      '@type': 'Offer',
      priceCurrency: product.currency,
      // schema.org wants a major-unit decimal. This is the one place a minor-unit figure becomes one,
      // and it is for display to a crawler — never for arithmetic (CLAUDE.md rule 2).
      price: (int64(product.fromPriceMinor) / 100).toFixed(2),
      availability: 'https://schema.org/InStock',
      seller: { '@type': 'Organization', name: sellerName },
    },
  };
}
