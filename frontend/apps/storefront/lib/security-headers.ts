/**
 * The response headers every page of an agency's website carries (issue 107).
 *
 * Two halves, because they are set in two places:
 *
 * - **The fixed headers** are the same on every response, so `next.config.ts` sets them for every
 *   path, static assets included.
 * - **The Content-Security-Policy** carries a fresh nonce per request, so `middleware.ts` builds it.
 *   Next.js reads the nonce back out of the request's policy and puts it on every script it writes,
 *   which is what lets the policy refuse any script it did not write.
 *
 * **The branding trap.** These headers are served on the agency's own domain, where anyone can read
 * them. Nothing here may name the platform (CLAUDE.md rule 4): the policy lists `'self'`, schemes and
 * the payment gateway, never one of our hosts. Images are allowed from any `https:` origin for the
 * same reason — naming the asset host would print our domain into every traveller's response.
 */

/** Where the gateway's hosted payment page lives. Checkout redirects there after a form post. */
const PAYMENT_PAGE_ORIGIN = 'https://checkout.paystack.com';

/**
 * Thirty days, without `includeSubDomains` or `preload`. The domain is the agency's: its other
 * subdomains are not ours to pin, preload is close to irreversible, and a certificate that lapses
 * on a custom domain should not lock travellers out for a year.
 */
export const STOREFRONT_HSTS = 'max-age=2592000';

export const fixedSecurityHeaders: { key: string; value: string }[] = [
  { key: 'Strict-Transport-Security', value: STOREFRONT_HSTS },
  { key: 'X-Content-Type-Options', value: 'nosniff' },
  // The gateway learns which site a traveller came from, and nothing about the page they were on.
  { key: 'Referrer-Policy', value: 'strict-origin-when-cross-origin' },
  { key: 'X-Frame-Options', value: 'DENY' },
  { key: 'Permissions-Policy', value: 'camera=(), microphone=(), geolocation=(), usb=()' },
];

/**
 * The policy for one response.
 *
 * - `script-src` trusts only scripts carrying this response's nonce, and what they load
 *   (`'strict-dynamic'`). An injected `<script>` has no nonce and does not run.
 * - `style-src 'unsafe-inline'`, because the agency's brand colour is written as a `style` attribute
 *   on `<body>` (see `app/layout.tsx`). Styles cannot run code; scripts are what the policy is for.
 * - `form-action` names the payment page: a checkout without JavaScript posts the form and is
 *   redirected there, and browsers apply `form-action` to that redirect.
 * - Development adds what `next dev` needs: `'unsafe-eval'` for React's debugging, a websocket for
 *   hot reload, and plain-http images from the API on localhost.
 */
export function contentSecurityPolicy(nonce: string, development: boolean): string {
  const directives: [string, ...string[]][] = [
    ['default-src', "'self'"],
    [
      'script-src',
      "'self'",
      `'nonce-${nonce}'`,
      "'strict-dynamic'",
      ...(development ? ["'unsafe-eval'"] : []),
    ],
    ['style-src', "'self'", "'unsafe-inline'"],
    ['img-src', "'self'", 'data:', 'blob:', 'https:', ...(development ? ['http:'] : [])],
    ['font-src', "'self'", 'data:'],
    ['connect-src', "'self'", ...(development ? ['ws:'] : [])],
    ['form-action', "'self'", PAYMENT_PAGE_ORIGIN],
    ['frame-ancestors', "'none'"],
    ['base-uri', "'self'"],
    ['object-src', "'none'"],
  ];

  return directives.map((directive) => directive.join(' ')).join('; ');
}

/** A nonce: 16 random bytes, base64. Unguessable, and new for every response. */
export function createNonce(): string {
  const bytes = new Uint8Array(16);
  crypto.getRandomValues(bytes);
  return btoa(String.fromCharCode(...bytes));
}
