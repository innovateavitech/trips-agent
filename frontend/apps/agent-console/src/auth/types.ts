/**
 * Session shapes.
 *
 * These are hand-written ON PURPOSE and only for the session — see the note in
 * `src/api/README.md`. Everything else waits for the generated client. When
 * `packages/api-client` exists, delete these and import the generated `MeResponse`.
 */

/** An agency the signed-in user can act as. Principals see several. */
export interface AgencyRef {
  id: string;
  name: string;
  /** Gates the console: an unverified agency cannot transact. See #19 / #20. */
  kybStatus: 'unverified' | 'pending' | 'verified' | 'rejected';
}

export interface SessionUser {
  id: string;
  fullName: string;
  email: string;
  roles: string[];
}

export interface Session {
  user: SessionUser;
  /** The agency whose data the console is currently showing. */
  activeAgency: AgencyRef;
  /** Every agency this user may switch to. One entry for a normal agent. */
  agencies: AgencyRef[];
}
