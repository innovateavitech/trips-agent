import { NextResponse, type NextRequest } from 'next/server';

/**
 * Carries a preview link's token from the URL into every request of that visit (issue 58's staging
 * preview).
 *
 * A preview link is `https://their-site.example/?preview=<token>`, but only the first page load
 * carries the parameter — and a layout, which is where the site is fetched, never sees a query
 * string at all. So the token is moved once into a cookie and read back as a header on every
 * request after it.
 *
 * The cookie is `httpOnly` and lasts the browser session: it is a capability to see an unpublished
 * draft, so no script needs to read it and nothing should keep it after the tab closes.
 */

/** The query parameter on the link the console generates. */
const PREVIEW_PARAM = 'preview';

/** Where the token is kept, and the header the app reads it back from. */
const PREVIEW_COOKIE = 'storefront-preview';
export const PREVIEW_HEADER = 'x-storefront-preview-token';

export function middleware(request: NextRequest): NextResponse {
  const fromUrl = request.nextUrl.searchParams.get(PREVIEW_PARAM);
  const token = fromUrl ?? request.cookies.get(PREVIEW_COOKIE)?.value;

  const headers = new Headers(request.headers);

  // Never trust an inbound header of this name: only this middleware may set it, from the URL or
  // from the cookie it wrote. Otherwise anyone could send one and ask for a draft.
  headers.delete(PREVIEW_HEADER);

  if (token) {
    headers.set(PREVIEW_HEADER, token);
  }

  const response = NextResponse.next({ request: { headers } });

  if (fromUrl) {
    response.cookies.set(PREVIEW_COOKIE, fromUrl, {
      httpOnly: true,
      sameSite: 'lax',
      secure: request.nextUrl.protocol === 'https:',
      path: '/',
    });
  }

  return response;
}

export const config = {
  // Everything but Next's own assets and the rebuild endpoint, which has its own authentication.
  matcher: ['/((?!_next/static|_next/image|api/revalidate|favicon.ico).*)'],
};
