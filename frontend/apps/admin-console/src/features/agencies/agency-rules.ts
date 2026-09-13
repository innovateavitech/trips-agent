import {
  AGENCY_EXPORT_PERMISSION,
  AGENCY_MANAGE_PERMISSION,
  AGENCY_SUSPEND_PERMISSION,
  AGENCY_TERMINATE_PERMISSION,
  KYB_REVIEW_PERMISSION,
} from '../../lib/auth/claims';
import type { AgencyAction, AgencyProfile, AgencyStatus } from './types';

/**
 * What a status means, said the same way on every screen.
 *
 * The tones come from the design system's badge variants — there is no colour in this file, and
 * there is none anywhere outside tokens.css.
 */
export type Tone = 'neutral' | 'success' | 'warning' | 'destructive' | 'info';

export interface StatusDisplay {
  label: string;
  tone: Tone;
  /** One sentence explaining what the agency can and cannot do. */
  meaning: string;
}

export function agencyStatusDisplay(status: AgencyStatus): StatusDisplay {
  switch (status) {
    case 'Verified':
      return {
        label: 'Verified',
        tone: 'success',
        meaning: 'Selling normally. The storefront is live and new bookings are accepted.',
      };
    case 'PendingVerification':
      return {
        label: 'Awaiting verification',
        tone: 'warning',
        meaning: 'KYB has not been decided. They can prepare, but cannot fund a wallet or sell.',
      };
    case 'Rejected':
      return {
        label: 'Verification refused',
        tone: 'destructive',
        meaning: 'KYB was turned down. They can correct the submission and send it again.',
      };
    case 'Suspended':
      return {
        label: 'Suspended',
        tone: 'destructive',
        // Build-plan decision 14, in the words an admin needs when they are looking at the screen.
        meaning:
          'No new bookings and the storefront is offline. Bookings already made stand, and ' +
          'travellers keep their documents.',
      };
    case 'Terminated':
      return {
        label: 'Terminated',
        tone: 'neutral',
        meaning: 'The relationship has ended. The record is kept for orders, invoices and tax.',
      };
    default:
      return { label: status, tone: 'neutral', meaning: '' };
  }
}

/** The four lifecycle actions, and everything a screen needs to offer one. */
export interface ActionDisplay {
  action: AgencyAction;
  label: string;
  /** The permission the API requires. Shown as a refusal here before it is refused there. */
  permission: string;
  /** The heading on the confirmation. */
  title: string;
  /** What will happen, in full, before they confirm. */
  consequences: string[];
  /** The label on the button that actually does it. */
  confirmLabel: string;
  destructive: boolean;
}

const ACTIONS: Record<AgencyAction, ActionDisplay> = {
  verify: {
    action: 'verify',
    label: 'Verify',
    permission: KYB_REVIEW_PERMISSION,
    title: 'Verify this agency by hand',
    consequences: [
      'They can fund a wallet and start selling immediately.',
      'Their storefront goes live.',
      'The ordinary route is the KYB queue. Say here why this one is being done by hand.',
    ],
    confirmLabel: 'Verify agency',
    destructive: false,
  },
  suspend: {
    action: 'suspend',
    label: 'Suspend',
    permission: AGENCY_SUSPEND_PERMISSION,
    title: 'Suspend this agency',
    // Decision 14, spelled out. An admin should never have to guess which half of the business
    // stops, and travellers with forward bookings are the half that does not.
    consequences: [
      'No new bookings, in the console or on the storefront.',
      'Their storefront stops serving.',
      'Bookings already made stand — nothing is cancelled or refunded.',
      'Travellers keep their tickets and vouchers through their magic link.',
      'Their staff can still sign in to look after those travellers.',
    ],
    confirmLabel: 'Suspend agency',
    destructive: true,
  },
  reinstate: {
    action: 'reinstate',
    label: 'Lift suspension',
    permission: AGENCY_SUSPEND_PERMISSION,
    title: 'Lift this suspension',
    consequences: [
      'They can sell again, and their storefront comes back.',
      'An agency suspended before KYB was approved goes back to awaiting a decision, not past it.',
    ],
    confirmLabel: 'Lift suspension',
    destructive: false,
  },
  terminate: {
    action: 'terminate',
    label: 'Terminate',
    permission: AGENCY_TERMINATE_PERMISSION,
    title: 'Terminate this agency',
    consequences: [
      'The relationship ends. This is not reversible from the console.',
      'No new bookings, and the storefront stops serving.',
      'The record is kept — orders, invoices and ledger entries still point at it.',
      'Take the data export first. They are entitled to their own records.',
    ],
    confirmLabel: 'Terminate agency',
    destructive: true,
  },
};

export function actionDisplay(action: AgencyAction): ActionDisplay {
  return ACTIONS[action];
}

/**
 * Which actions make sense from where the agency is now.
 *
 * The API refuses the rest with a 409 regardless; this is so the screen does not offer a button
 * whose only outcome is an error.
 */
export function availableActions(status: AgencyStatus): AgencyAction[] {
  switch (status) {
    case 'Verified':
      return ['suspend', 'terminate'];
    case 'PendingVerification':
    case 'Rejected':
      return ['verify', 'suspend', 'terminate'];
    case 'Suspended':
      return ['reinstate', 'terminate'];
    case 'Terminated':
      return [];
    default:
      return [];
  }
}

/**
 * The reason rule, which now lives in `lib/reason.ts` because the back-office user screens demand
 * one too and there must only be one definition of what counts as an answer. Re-exported here so
 * the agency screens keep importing it from the rules file they already read.
 */
export { MAX_REASON_LENGTH, MIN_REASON_LENGTH, validateReason } from '../../lib/reason';

/** Whether this account may edit an agency's details at all. */
export function canEdit(permissions: string[]): boolean {
  return permissions.includes(AGENCY_MANAGE_PERMISSION);
}

/** Whether this account may take an export. It contains travellers' personal data. */
export function canExport(permissions: string[]): boolean {
  return permissions.includes(AGENCY_EXPORT_PERMISSION);
}

/**
 * What the profile's banner should say, or null when there is nothing to warn about.
 *
 * Only the statuses that stop an agency trading raise one. A verified agency needs no banner —
 * a screen that warns about everything warns about nothing.
 */
export function profileWarning(profile: AgencyProfile): { title: string; detail: string } | null {
  if (profile.canTakeNewBookings) return null;

  const display = agencyStatusDisplay(profile.status);

  return {
    title: `${profile.name} is ${display.label.toLowerCase()}`,
    detail: display.meaning,
  };
}
