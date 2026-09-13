import type { PlatformRole, PlatformUser, PlatformUserStatus } from './types';

export type Tone = 'neutral' | 'success' | 'warning' | 'destructive' | 'info';

export interface StatusDisplay {
  label: string;
  tone: Tone;
  /** One sentence saying what this account can do right now. */
  meaning: string;
}

export function userStatusDisplay(status: PlatformUserStatus): StatusDisplay {
  switch (status) {
    case 'Active':
      return {
        label: 'Active',
        tone: 'success',
        meaning: 'Signs in and works normally.',
      };
    case 'Invited':
      return {
        label: 'Invited',
        tone: 'warning',
        meaning: 'Has not set a password yet, so cannot sign in. The invitation is outstanding.',
      };
    case 'Suspended':
      return {
        label: 'Suspended',
        tone: 'destructive',
        meaning: 'Cannot sign in. Everything they did is kept, and the account can be reactivated.',
      };
    case 'Deactivated':
      return {
        label: 'Deactivated',
        tone: 'neutral',
        meaning: 'Switched off for good, and kept so their past actions still have an actor.',
      };
    default:
      return { label: status, tone: 'neutral', meaning: '' };
  }
}

/**
 * The status a person can be moved to from where they are, and what the button should say.
 *
 * Only the reversible pair is offered. Deactivating is the one that cannot be undone from this
 * screen, and putting it beside "Suspend" on every row is how somebody ends a colleague's account
 * when they meant to pause it — so it is not here at all.
 */
export function nextStatus(
  status: PlatformUserStatus,
): { to: PlatformUserStatus; label: string; destructive: boolean } | null {
  switch (status) {
    case 'Active':
    case 'Invited':
      return { to: 'Suspended', label: 'Suspend', destructive: true };
    case 'Suspended':
      return { to: 'Active', label: 'Reactivate', destructive: false };
    default:
      return null;
  }
}

/**
 * Whether this row is the person looking at it.
 *
 * The API refuses somebody suspending their own account; the screen stops offering it first, so
 * nobody has to learn the rule from a rejection. Compared on the id rather than the email, which
 * can be changed.
 */
export function isSelf(user: PlatformUser, signedInUserId: string | null): boolean {
  return signedInUserId !== null && user.id === signedInUserId;
}

/** The full name, or the email when a name was never filled in. */
export function displayName(user: PlatformUser): string {
  const name = `${user.firstName} ${user.lastName}`.trim();
  return name === '' ? user.email : name;
}

/**
 * What a role lets somebody do, in plain words, for the invite form.
 *
 * Built from the permissions the API sends rather than from a list in the console: a role whose
 * permissions change in the seeder would otherwise keep describing itself the old way here, which
 * is exactly the kind of quiet lie this screen must not tell. Anything not named is left out
 * rather than guessed at — the count says how many are not shown.
 */
const PERMISSION_SUMMARIES: Record<string, string> = {
  'kyb.review': 'decide KYB submissions',
  'agency.view': 'read every agency',
  'agency.manage': 'edit an agency and verify it',
  'agency.suspend': 'suspend and reinstate an agency',
  'agency.terminate': 'terminate an agency',
  'agency.export': "export an agency's data",
  'audit.view': 'read the audit log',
  'platform.report.view': 'read the platform dashboard',
  'subscription.manage': 'change an agency’s plan',
  'platform.user.manage': 'create and change back-office accounts',
};

export interface RoleSummary {
  /** The named abilities, in the order the list above gives them. */
  can: string[];
  /** Permissions the console has no wording for. Counted so the summary is never a half-truth. */
  unnamedCount: number;
}

export function summarise(role: PlatformRole): RoleSummary {
  const named = Object.keys(PERMISSION_SUMMARIES).filter((code) => role.permissions.includes(code));

  return {
    can: named.map((code) => PERMISSION_SUMMARIES[code] as string),
    unnamedCount: role.permissions.length - named.length,
  };
}

/**
 * Whether a role can change anything at all about an agency.
 *
 * Used to mark the read-only roles in the list, because "Support Admin" does not itself tell a
 * Super Admin that the account they are about to create cannot suspend anybody.
 */
const CHANGES_AN_AGENCY = [
  'agency.manage',
  'agency.suspend',
  'agency.terminate',
  'kyb.review',
  'platform.user.manage',
];

export function isReadOnly(role: PlatformRole): boolean {
  return !CHANGES_AN_AGENCY.some((code) => role.permissions.includes(code));
}
