import { headers } from 'next/headers';
import type { Schemas } from '@trips/api-client';

/**
 * Reading the traveller-facing API (issue 60).
 *
 * Two rules hold this file together.
 *
 * **The hostname decides whose site is served, and nothing else.** Every page here is rendered on
 * our server, so the request the API sees comes from us, not from the traveller — its `Host` would
 * be the API's own. The traveller's hostname is forwarded instead, and the API believes that header
 * only from a proxy it is configured to trust. Nothing in a URL, a cookie or a query string can
 * choose an agency.
 *
 * **Nothing here is cached by time alone.** A publish has to be visible on the next page load, not
 * whenever a timer runs out, so every response is tagged by site and by hostname and the API asks
 * us to drop those tags when a site is published (`app/api/revalidate`). The long `revalidate` is
 * only a backstop for a rebuild request that never arrived.
 */

export type Site = Schemas['PublicSiteResponse'];
export type Catalog = Schemas['PublicCatalogResponse'];
export type Product = Schemas['PublicProductResponse'];
export type ProductSummary = Schemas['PublicProductSummary'];
export type Sitemap = Schemas['PublicSitemapResponse'];
export type Block = Schemas['SiteBlockDto'];
export type Page = Schemas['SiteSnapshotPage'];

/** The backstop, in seconds. Publishing revalidates by tag long before this. */
const FALLBACK_REVALIDATE = 3600;

/** The header the preview link's token travels in. Set by us, never by a traveller. */
const PREVIEW_HEADER = 'X-Storefront-Preview';

/**
 * The header `middleware.ts` puts a preview link's token in. Only the middleware sets it — an
 * inbound one is stripped there — so reaching a draft always means holding a signed link.
 */
const PREVIEW_TOKEN_HEADER = 'x-storefront-preview-token';

/** The preview token for this request, when the visitor followed a preview link. */
export async function previewToken(): Promise<string | undefined> {
  const incoming = await headers();

  return incoming.get(PREVIEW_TOKEN_HEADER) ?? undefined;
}

function apiBaseUrl(): string {
  const base = process.env.API_BASE_URL ?? process.env.NEXT_PUBLIC_API_BASE_URL;

  if (!base) {
    throw new Error(
      'API_BASE_URL is not set, so the storefront has no API to read. See .env.example.',
    );
  }

  return base.replace(/\/$/, '');
}

/**
 * The hostname this request arrived on, lower-cased and without its port — the same shape the
 * domains table stores.
 */
export async function requestHostname(): Promise<string> {
  const incoming = await headers();
  const host = incoming.get('x-forwarded-host') ?? incoming.get('host') ?? '';

  return host.split(':')[0]!.trim().toLowerCase();
}

/** The cache tags a response for one hostname carries. */
function tagsFor(hostname: string, siteId?: string): string[] {
  return siteId ? [`host:${hostname}`, `site:${siteId}`] : [`host:${hostname}`];
}

async function get<T>(
  path: string,
  hostname: string,
  options: { siteId?: string; previewToken?: string } = {},
): Promise<T | null> {
  const requestHeaders: Record<string, string> = {
    // The traveller's hostname, which is what the API resolves the agency from.
    'X-Forwarded-Host': hostname,
    Accept: 'application/json',
  };

  if (options.previewToken) {
    requestHeaders[PREVIEW_HEADER] = options.previewToken;
  }

  const response = await fetch(`${apiBaseUrl()}/api/v1/public/storefront${path}`, {
    headers: requestHeaders,
    // A preview is somebody looking at an unpublished draft: caching it would show them a version
    // they have already moved on from, and could leak it to the next visitor on the same host.
    ...(options.previewToken
      ? { cache: 'no-store' as const }
      : {
          next: { revalidate: FALLBACK_REVALIDATE, tags: tagsFor(hostname, options.siteId) },
        }),
  });

  if (response.status === 404) {
    return null;
  }

  if (!response.ok) {
    throw new Error(`The storefront API answered ${response.status} for ${path}.`);
  }

  return (await response.json()) as T;
}

/**
 * The site on this request's hostname, or null when no site answers there.
 *
 * Shows the published version, or the draft a preview link names — the API honours a token only for
 * the site on that same hostname, so a leaked link cannot be replayed against a stranger's.
 */
export async function getSite(): Promise<Site | null> {
  const [hostname, token] = await Promise.all([requestHostname(), previewToken()]);

  return get<Site>('/site', hostname, { previewToken: token });
}

/** One page of the catalog, narrowed by whatever the traveller chose. */
export async function getCatalog(
  siteId: string,
  query: Record<string, string | undefined>,
): Promise<Catalog | null> {
  const search = new URLSearchParams();

  for (const [key, value] of Object.entries(query)) {
    if (value) {
      search.set(key, value);
    }
  }

  const suffix = search.size > 0 ? `?${search.toString()}` : '';

  return get<Catalog>(`/catalog${suffix}`, await requestHostname(), { siteId });
}

/** One product's page, or null when the agency has no such product on sale. */
export async function getProduct(siteId: string, slug: string): Promise<Product | null> {
  return get<Product>(`/catalog/${encodeURIComponent(slug)}`, await requestHostname(), { siteId });
}

/**
 * The products one product-grid block should show.
 *
 * The block is named by where it sits — which page, and which block on it — rather than by what it
 * should contain. Whether that grid means "the newest six tours" or "these four, in this order" is
 * settled by the published snapshot on the server, so a traveller cannot ask a site to show products
 * its owner did not put there.
 */
export async function getGrid(
  siteId: string,
  pageSlug: string,
  blockIndex: number,
): Promise<ProductSummary[]> {
  const search = new URLSearchParams({ page: pageSlug, block: String(blockIndex) });

  const grid = await get<{ products: ProductSummary[] }>(
    `/grid?${search.toString()}`,
    await requestHostname(),
    { siteId },
  );

  return grid?.products ?? [];
}

/**
 * Every product-grid block on a page, resolved in one pass and keyed by the block's position — the
 * shape `BlockList` reads them back with.
 */
export async function getGridsFor(
  siteId: string,
  page: Page,
): Promise<Record<number, ProductSummary[]>> {
  const grids = await Promise.all(
    page.blocks.map(async (block, index) =>
      block.type === 'ProductGrid'
        ? ([index, await getGrid(siteId, page.slug, index)] as const)
        : null,
    ),
  );

  return Object.fromEntries(grids.filter((entry) => entry !== null));
}

/** Every address on the site, for `sitemap.xml`. */
export async function getSitemap(): Promise<Sitemap | null> {
  return get<Sitemap>('/sitemap', await requestHostname());
}
