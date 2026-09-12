import {
  AGENCY_VIEW_PERMISSION,
  KYB_REVIEW_PERMISSION,
  PLATFORM_REPORT_PERMISSION,
  hasPermission,
  type StaffClaims,
} from './claims';

/**
 * Where somebody lands when they were not heading anywhere in particular and we know nothing
 * about them yet — during sign-in, before the token has been read.
 *
 * Every back-office role holds `agency.view`, so the directory is the one screen that is never a
 * refusal. Once the claims are known, `homePathFor` picks something more useful.
 */
export const HOME_PATH = '/agencies';

/**
 * The first screen this account should see.
 *
 * In order of what the role is for rather than of importance: a Super Admin or Finance admin wants
 * the numbers, an Operations admin wants the queue that is waiting for them, and Support — who
 * holds neither — wants the agency they are about to be asked about. Sending everybody to one
 * fixed page would land Support on a refusal every time they sign in.
 */
export function homePathFor(claims: StaffClaims | null | undefined): string {
  if (!claims) return HOME_PATH;
  if (hasPermission(claims, PLATFORM_REPORT_PERMISSION)) return '/dashboard';
  if (hasPermission(claims, KYB_REVIEW_PERMISSION)) return '/kyb';
  if (hasPermission(claims, AGENCY_VIEW_PERMISSION)) return '/agencies';
  return HOME_PATH;
}

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
