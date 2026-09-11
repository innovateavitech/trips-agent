/**
 * Where the auth guard sends people, and where sign-in sends them back.
 *
 * The intended destination travels in the URL (`/sign-in?next=/wallet`) rather
 * than in router state, so it survives a reload of the sign-in page and a
 * trip through "forgot password" and back.
 */

export const SIGN_IN_PATH = '/sign-in';

/** Why the agent is looking at the sign-in page, when it is worth telling them. */
export type SignInReason = 'expired' | 'unavailable';

/** Where to go when there is no usable `next`. */
export const DEFAULT_DESTINATION = '/';

/**
 * The sign-in URL for someone who tried to open `destination`.
 *
 * `destination` is the full in-app location — path, query and hash — so an
 * agent bounced from `/bookings?status=failed` lands back on the filtered list,
 * not on an unfiltered one.
 */
export function signInPathFor(destination: string, reason?: SignInReason): string {
  const params = new URLSearchParams();
  const next = safeNextPath(destination);
  if (next !== DEFAULT_DESTINATION) params.set('next', next);
  if (reason) params.set('reason', reason);

  const query = params.toString();
  return query ? `${SIGN_IN_PATH}?${query}` : SIGN_IN_PATH;
}

/**
 * Validates a `next` value before navigating to it.
 *
 * It arrives from the address bar, so it is attacker-controlled: a phishing
 * email can link to `/sign-in?next=https://trips-login.example`. After a real
 * sign-in on our real page, an unchecked redirect would hand the agent — now
 * trusting the tab — to someone else's site. So only same-origin, in-app paths
 * are allowed, and anything else quietly becomes the dashboard.
 */
export function safeNextPath(raw: string | null | undefined): string {
  if (!raw) return DEFAULT_DESTINATION;

  // Must be a path on this origin. `//evil.com` and `/\evil.com` are
  // protocol-relative URLs that browsers treat as another host.
  if (!raw.startsWith('/') || raw.startsWith('//') || raw.startsWith('/\\')) {
    return DEFAULT_DESTINATION;
  }

  // Resolving against a throwaway origin catches anything that escapes it —
  // encoded tricks included — without trusting a hand-written pattern.
  let url: URL;
  try {
    url = new URL(raw, 'https://console.invalid');
  } catch {
    return DEFAULT_DESTINATION;
  }
  if (url.origin !== 'https://console.invalid') return DEFAULT_DESTINATION;

  // Sending someone "back" to sign-in after signing in would be a loop.
  if (url.pathname === SIGN_IN_PATH) return DEFAULT_DESTINATION;

  return `${url.pathname}${url.search}${url.hash}`;
}

export function readSignInReason(raw: string | null): SignInReason | null {
  return raw === 'expired' || raw === 'unavailable' ? raw : null;
}
