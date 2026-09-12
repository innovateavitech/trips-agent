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
export type Quote = Schemas['PublicQuoteResponse'];
export type TripRequest = Schemas['TripRequestSubmission'];
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

/**
 * The CRM's anonymous endpoints, which live beside the storefront's own (issue 62).
 *
 * Same rule as everything else here: the agency is decided by the hostname the traveller used, which
 * this sends on as `X-Storefront-Host`. Nothing is cached — a trip request is a write, and a quote is
 * one customer's, read once and then marked as opened.
 */
async function crm<T>(
  path: string,
  init: { method: 'GET' | 'POST'; body?: unknown },
): Promise<{ ok: boolean; status: number; data: T | null }> {
  const hostname = await requestHostname();

  const response = await fetch(`${apiBaseUrl()}/api/v1/public/crm${path}`, {
    method: init.method,
    headers: {
      'X-Storefront-Host': hostname,
      Accept: 'application/json',
      ...(init.body === undefined ? {} : { 'Content-Type': 'application/json' }),
    },
    body: init.body === undefined ? undefined : JSON.stringify(init.body),
    cache: 'no-store',
  });

  // A 202 to a trip request has no body, and a refusal has a problem document rather than the type
  // asked for. The caller is given the status either way and decides what to say about it.
  const data = response.headers.get('content-type')?.includes('json')
    ? ((await response.json()) as T)
    : null;

  return { ok: response.ok, status: response.status, data };
}

/** Sends a traveller's enquiry to the agency. Accepted means it is in their CRM as a new lead. */
export async function submitTripRequest(request: TripRequest): Promise<{ ok: boolean }> {
  const { ok } = await crm<unknown>('/trip-requests', { method: 'POST', body: request });

  return { ok };
}

/** One customer's quote, by the token in their link. Reading it records that they opened it. */
export async function getQuote(token: string): Promise<Quote | null> {
  const { ok, data } = await crm<Quote>(`/quotes/${encodeURIComponent(token)}`, { method: 'GET' });

  return ok ? data : null;
}

/** The customer's answer to a quote. */
export async function respondToQuote(
  token: string,
  answer: 'accept' | 'decline',
  reason?: string,
): Promise<{ ok: boolean }> {
  const { ok } = await crm<unknown>(`/quotes/${encodeURIComponent(token)}/${answer}`, {
    method: 'POST',
    body: answer === 'decline' ? { reason: reason ?? null } : undefined,
  });

  return { ok };
}

/** Every address on the site, for `sitemap.xml`. */
export async function getSitemap(): Promise<Sitemap | null> {
  return get<Sitemap>('/sitemap', await requestHostname());
}

// ---------------------------------------------------------------------------- the buying flow

export type Cart = Schemas['CartResponse'];
export type CartItem = Schemas['CartItemResponse'];
export type AddToCart = Schemas['AddCartItemRequest'];
export type BeginCheckout = Schemas['BeginCheckoutRequest'];
export type CheckoutStarted = Schemas['BeginCheckoutResponse'];
export type CheckoutStatus = Schemas['CheckoutStatusResponse'];
export type Booking = Schemas['ManageBookingResponse'];
export type Departure = Schemas['PublicDepartureResponse'];

/** What a commerce call came to: what it returned, and what to say when it did not work. */
export interface StoreCall<T> {
  ok: boolean;
  status: number;
  data: T | null;
  /** The agency-facing title and detail of a refusal, so a page can say what really happened. */
  problem?: { title?: string; detail?: string; errors?: Record<string, string[]> };
}

/**
 * The traveller's cart, checkout and booking endpoints (build plan F5).
 *
 * Same two rules as everything else here: the agency comes from the hostname the traveller used, and
 * nothing is cached — a cart is one browser's, and a checkout is a write.
 *
 * The cart's session token travels in `X-Cart-Session`. It is kept in an http-only cookie rather
 * than in the page, because it is the whole of a guest's identity (there are no traveller accounts)
 * and a script on the page has no business reading it.
 */
export async function commerce<T>(
  path: string,
  init: {
    method: 'GET' | 'POST' | 'DELETE';
    body?: unknown;
    sessionToken?: string;
  },
): Promise<StoreCall<T>> {
  const hostname = await requestHostname();

  const requestHeaders: Record<string, string> = {
    'X-Storefront-Host': hostname,
    Accept: 'application/json',
  };

  if (init.sessionToken) {
    requestHeaders['X-Cart-Session'] = init.sessionToken;
  }

  if (init.body !== undefined) {
    requestHeaders['Content-Type'] = 'application/json';
  }

  const response = await fetch(`${apiBaseUrl()}/api/v1/public${path}`, {
    method: init.method,
    headers: requestHeaders,
    body: init.body === undefined ? undefined : JSON.stringify(init.body),
    cache: 'no-store',
  });

  const payload = response.headers.get('content-type')?.includes('json')
    ? await response.json()
    : null;

  return response.ok
    ? { ok: true, status: response.status, data: payload as T }
    : {
        ok: false,
        status: response.status,
        data: null,
        problem: (payload ?? undefined) as StoreCall<T>['problem'],
      };
}

/** The dated departures on one trip, priced for this party. */
export async function getDepartures(
  productSlug: string,
  party: { adults: number; children: number; infants: number },
): Promise<Departure[]> {
  const search = new URLSearchParams({
    adults: String(party.adults),
    children: String(party.children),
    infants: String(party.infants),
  });

  const { data } = await commerce<Departure[]>(
    `/trips/${encodeURIComponent(productSlug)}/departures?${search.toString()}`,
    { method: 'GET' },
  );

  return data ?? [];
}

/** One booking, by the secret in the traveller's link. */
export async function getBooking(token: string): Promise<Booking | null> {
  const { data } = await commerce<Booking>(`/bookings/${encodeURIComponent(token)}`, {
    method: 'GET',
  });

  return data;
}
