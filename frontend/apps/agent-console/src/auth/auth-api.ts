/**
 * Everything the shell needs from the server about WHO is using it, as one
 * port — the same pattern as `WalletApi`.
 *
 * Two adapters implement it:
 *   - `http-auth-api.ts` — the real `/api/v1/auth` endpoints. The default.
 *   - `mock/mock-auth-api.ts` — a stand-in for demos, chosen with
 *     `VITE_AUTH_MODE=mock`. See `app/settings.ts`.
 *
 * Nothing outside `auth/` knows which one is running.
 */

export interface Credentials {
  email: string;
  password: string;
}

/** An agency this person can act for. */
export interface AgencyOption {
  id: string;
  name: string;
  /** `principal` owns sub-agents; a `sub_agent` sells under a principal. */
  kind: 'principal' | 'sub_agent';
}

export interface SessionUser {
  userId: string;
  email: string;
  roles: string[];
  /**
   * The agency every request is currently scoped to — the `agency_id` in the
   * token, which the API's tenant filter reads. `null` for a platform user
   * with no agency, who has no business in this console.
   */
  agency: AgencyOption | null;
}

export interface AuthApi {
  /** Rejects with an `ApiError` carrying the API's message on bad credentials. */
  signIn(credentials: Credentials): Promise<SessionUser>;

  /** On page load: the session this tab already had, or `null` if none. */
  restore(): Promise<SessionUser | null>;

  /** Always resolves. A sign-out that fails server-side still ends it here. */
  signOut(): Promise<void>;

  /** Every agency this user may switch to, the current one included. */
  listAgencies(): Promise<AgencyOption[]>;

  /**
   * Re-scopes the session to another agency. The server has to issue a token
   * for it — the tenant filter trusts the token, never the browser.
   */
  switchAgency(agencyId: string): Promise<SessionUser>;
}

/** Friendly name for the top bar: the part of the email before the @. */
export function displayNameFor(user: Pick<SessionUser, 'email'>): string {
  const local = user.email.split('@')[0] ?? '';
  const words = local
    .split(/[._-]+/)
    .filter(Boolean)
    .map((word) => word.charAt(0).toUpperCase() + word.slice(1));
  return words.length > 0 ? words.join(' ') : user.email;
}

/** Up to two letters for the avatar: "adaeze.okafor@…" → "AO". */
export function initialsFor(user: Pick<SessionUser, 'email'>): string {
  const name = displayNameFor(user);
  const letters = name
    .split(' ')
    .map((word) => word.charAt(0))
    .join('')
    .slice(0, 2)
    .toUpperCase();
  return letters || '?';
}

/**
 * Roles arrive from the token by name — the system roles are `Owner`, `Manager`
 * and `Agent` (backend `Role.SystemRoles`). They already read well; anything
 * else is tidied from `snake_case` so a custom role never shows as a code.
 */
export function roleLabel(role: string): string {
  const words = role.replace(/[_-]+/g, ' ').trim();
  return words.charAt(0).toUpperCase() + words.slice(1);
}
