import { int64 } from '@trips/utils';
import type { Metadata } from 'next';
import Link from 'next/link';
import { notFound } from 'next/navigation';
import { getCatalog, getSite } from '../../lib/api';
import { CatalogFilters } from '../../components/catalog-filters';
import { ProductCard } from '../../components/product-card';

/**
 * Everything the agency sells, with the filters a traveller can narrow it by.
 *
 * The address is `/tours` for every kind of product — tours, packages and visas alike — because it is
 * the address the published sitemap and the templates' own links already use. The `type` filter is
 * what separates them.
 */

interface Props {
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}

/** How the catalog is titled when the agency has not written a page of their own for it. */
const DEFAULT_TITLE = 'What we offer';

export async function generateMetadata({ searchParams }: Props): Promise<Metadata> {
  const [site, query] = await Promise.all([getSite(), searchParams]);
  const page = site?.content.pages.find((candidate) => candidate.slug === 'tours');
  const title = page?.metaTitle ?? page?.title ?? DEFAULT_TITLE;

  return {
    title,
    description: page?.metaDescription ?? undefined,
    alternates: { canonical: '/tours' },
    // A filtered view is the same products in a different order. Letting each combination be indexed
    // separately would put thousands of near-identical pages in front of a search engine.
    robots: Object.keys(query).length > 0 ? { index: false, follow: true } : undefined,
  };
}

export default async function CatalogPage({ searchParams }: Props) {
  const [site, rawQuery] = await Promise.all([getSite(), searchParams]);

  if (!site) {
    notFound();
  }

  const query = {
    type: single(rawQuery.type),
    destination: single(rawQuery.destination),
    category: single(rawQuery.category),
    maxPrice: single(rawQuery.maxPrice),
    q: single(rawQuery.q),
    page: single(rawQuery.page),
  };

  const catalog = await getCatalog(site.siteId, query);

  if (!catalog) {
    notFound();
  }

  const page = site.content.pages.find((candidate) => candidate.slug === 'tours');
  const currency = catalog.products[0]?.currency ?? 'NGN';
  const total = int64(catalog.total);
  const currentPage = int64(catalog.page);
  const pageCount = Math.max(1, Math.ceil(total / int64(catalog.pageSize)));

  return (
    <div className="mx-auto max-w-6xl px-4 py-12 sm:px-6">
      <h1 className="mb-2 text-3xl font-semibold tracking-tight text-foreground">
        {page?.title ?? DEFAULT_TITLE}
      </h1>
      <p className="mb-8 text-sm text-muted-foreground">
        {total === 1 ? '1 trip' : `${total} trips`}
      </p>

      <CatalogFilters filters={catalog.filters} selected={query} currency={currency} />

      {catalog.products.length === 0 ? (
        <p className="rounded-lg border border-border bg-card px-4 py-12 text-center text-sm text-muted-foreground">
          Nothing matches that just yet. Try a wider search, or get in touch and we will put
          something together for you.
        </p>
      ) : (
        <div className="grid gap-6 sm:grid-cols-2 lg:grid-cols-3">
          {catalog.products.map((product) => (
            <ProductCard key={product.slug} product={product} />
          ))}
        </div>
      )}

      {pageCount > 1 && (
        <nav aria-label="Pages of results" className="mt-10 flex items-center justify-center gap-4">
          {currentPage > 1 && (
            <PageLink query={query} page={currentPage - 1}>
              Previous
            </PageLink>
          )}
          <span className="text-sm text-muted-foreground">
            Page {currentPage} of {pageCount}
          </span>
          {currentPage < pageCount && (
            <PageLink query={query} page={currentPage + 1}>
              Next
            </PageLink>
          )}
        </nav>
      )}
    </div>
  );
}

function PageLink({
  query,
  page,
  children,
}: {
  query: Record<string, string | undefined>;
  page: number;
  children: React.ReactNode;
}) {
  const search = new URLSearchParams();

  for (const [key, value] of Object.entries({ ...query, page: String(page) })) {
    if (value) {
      search.set(key, value);
    }
  }

  return (
    <Link
      href={`/tours?${search.toString()}`}
      className="rounded-md border border-border px-4 py-2 text-sm font-medium text-foreground transition-colors hover:bg-muted"
    >
      {children}
    </Link>
  );
}

/** A query parameter given twice is a mistake or a probe; the first value is what we act on. */
function single(value: string | string[] | undefined): string | undefined {
  return Array.isArray(value) ? value[0] : value;
}
