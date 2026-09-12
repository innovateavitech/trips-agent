/**
 * Who the signed-in person is, read from their access token.
 *
 * IMPORTANT: none of this is security. The token is decoded, not verified — the browser does not
 * hold the signing key and must not. Every admin endpoint checks the same permission server-side
 * and answers 403 without it. Reading the claims here only lets the console show the right
 * screens and explain a refusal *before* the reviewer hits it.
 *
 * `/api/v1/auth/me` returns roles but not permissions, which is why this reads the token at all.
 * Adding permissions to that response is part of the session-hardening piece of epic #66.
 */

export interface StaffClaims {
  userId: string;
  email: string;
  roles: string[];
  permissions: string[];
  /** The agency the account belongs to. Always `null` for Trips staff. */
  agencyId: string | null;
}

/** Permission codes only platform roles hold — see PermissionCodes.PlatformOnly in the API. */
export const PLATFORM_PERMISSIONS = [
  'kyb.review',
  'agency.view',
  'agency.manage',
  'agency.suspend',
  'agency.terminate',
  'agency.export',
  'audit.view',
  'platform.report.view',
  'subscription.manage',
  'platform.user.manage',
] as const;

export const KYB_REVIEW_PERMISSION = 'kyb.review';

/**
 * The codes the back-office screens check, named rather than typed inline.
 *
 * None of this is security — the token is decoded, not verified, and every endpoint checks the
 * same permission server-side. Reading them here only lets the console show the right screens and
 * explain a refusal before somebody runs into it.
 */
export const AGENCY_VIEW_PERMISSION = 'agency.view';
export const AGENCY_MANAGE_PERMISSION = 'agency.manage';
export const AGENCY_SUSPEND_PERMISSION = 'agency.suspend';
export const AGENCY_TERMINATE_PERMISSION = 'agency.terminate';
export const AGENCY_EXPORT_PERMISSION = 'agency.export';
export const AUDIT_VIEW_PERMISSION = 'audit.view';
export const PLATFORM_REPORT_PERMISSION = 'platform.report.view';
export const PLATFORM_USER_PERMISSION = 'platform.user.manage';

/**
 * Decodes the claims in a JWT, or returns `null` when the token is not one we can read.
 *
 * A claim that occurs once arrives as a string and one that occurs several times as an array —
 * a staff member with a single role gets `"role": "Operations Admin"`, not `["Operations Admin"]`.
 * Both shapes are normalised to arrays so nothing downstream has to remember that.
 */
export function readClaims(token: string): StaffClaims | null {
  const parts = token.split('.');
  if (parts.length !== 3 || !parts[1]) return null;

  let payload: unknown;
  try {
    payload = JSON.parse(decodeBase64Url(parts[1]));
  } catch {
    return null;
  }

  if (typeof payload !== 'object' || payload === null) return null;
  const claims = payload as Record<string, unknown>;

  if (typeof claims.sub !== 'string' || claims.sub === '') return null;

  return {
    userId: claims.sub,
    email: typeof claims.email === 'string' ? claims.email : '',
    roles: asStrings(claims.role),
    permissions: asStrings(claims.permission),
    agencyId:
      typeof claims.agency_id === 'string' && claims.agency_id !== '' ? claims.agency_id : null,
  };
}

/**
 * Is this a Trips staff account, as opposed to someone working at a travel agency?
 *
 * Both conditions, not either. An agency account never carries a platform permission today, but
 * if a role were ever misconfigured to grant one, the missing `agency_id` is the second line.
 */
export function isTripsStaff(claims: StaffClaims): boolean {
  return (
    claims.agencyId === null &&
    claims.permissions.some((permission) =>
      (PLATFORM_PERMISSIONS as readonly string[]).includes(permission),
    )
  );
}

export function hasPermission(claims: StaffClaims, permission: string): boolean {
  return claims.permissions.includes(permission);
}

/** Two letters for the avatar: "ops@tripsagent.test" → "OP", "ada.okonkwo@…" → "AO". */
export function initialsFor(email: string): string {
  const local = email.split('@')[0] ?? '';
  const words = local.split(/[._-]+/).filter(Boolean);

  const letters =
    words.length >= 2 ? `${words[0]?.[0] ?? ''}${words[1]?.[0] ?? ''}` : local.slice(0, 2);

  return letters.toUpperCase() || '?';
}

function asStrings(value: unknown): string[] {
  if (typeof value === 'string') return [value];
  if (Array.isArray(value)) return value.filter((item): item is string => typeof item === 'string');
  return [];
}

/** base64url → UTF-8 text. `atob` alone would mangle any non-ASCII character in a name. */
function decodeBase64Url(segment: string): string {
  const base64 = segment.replace(/-/g, '+').replace(/_/g, '/');
  const padded = base64 + '='.repeat((4 - (base64.length % 4)) % 4);
  const bytes = Uint8Array.from(atob(padded), (char) => char.charCodeAt(0));
  return new TextDecoder().decode(bytes);
}
