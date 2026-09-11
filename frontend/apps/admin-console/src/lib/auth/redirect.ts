/** Where a reviewer lands after signing in when they were not heading anywhere in particular. */
export const HOME_PATH = '/kyb';

/**
 * The page to return to after sign-in, or the home page when `from` is not safe to follow.
 *
 * Only paths inside this app are followed. `//evil.example` is a *protocol-relative* URL — it
 * starts with a slash but leaves the site — and `/\evil.example` is treated the same way by some
 * browsers. Following either would turn the sign-in page into an open redirect that sends a
 * freshly signed-in staff member wherever an attacker's link pointed.
 */
export function safeRedirect(from: unknown): string {
  if (typeof from !== 'string') return HOME_PATH;
  if (!from.startsWith('/') || from.startsWith('//') || from.startsWith('/\\')) return HOME_PATH;
  if (from === '/sign-in' || from.startsWith('/sign-in?')) return HOME_PATH;
  return from;
}
