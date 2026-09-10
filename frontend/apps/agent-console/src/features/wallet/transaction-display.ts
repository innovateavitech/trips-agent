import type { BadgeProps } from '@trips/ui';
import type { WalletTransactionType } from './types';

/**
 * How each statement line is labelled and coloured.
 *
 * One table rather than a `switch` in the row component, so the statement, the
 * filter dropdown and the CSV export can never disagree about what a `hold` is
 * called — and so adding a transaction type is a single edit here.
 */

export const TRANSACTION_TYPE_LABELS: Record<WalletTransactionType, string> = {
  topup: 'Top-up',
  booking_payment: 'Booking',
  refund: 'Refund',
  hold: 'Hold',
  hold_release: 'Hold released',
  adjustment: 'Adjustment',
  reversal: 'Reversal',
};

/**
 * A short plain-English gloss for the types whose name does not explain itself.
 * Shown as the dropdown's helper text — "hold" means nothing to a new agent.
 */
export const TRANSACTION_TYPE_HINTS: Partial<Record<WalletTransactionType, string>> = {
  hold: 'Funds set aside for a booking that has not been ticketed yet',
  hold_release: 'Set-aside funds returned because a booking did not complete',
  reversal: 'A payment sent back after a booking failed',
};

export const TRANSACTION_TYPE_TONES: Record<
  WalletTransactionType,
  NonNullable<BadgeProps['tone']>
> = {
  topup: 'success',
  booking_payment: 'neutral',
  refund: 'info',
  hold: 'warning',
  hold_release: 'neutral',
  adjustment: 'neutral',
  reversal: 'destructive',
};

/** Every type, in the order the filter dropdown lists them. */
export const TRANSACTION_TYPES = Object.keys(TRANSACTION_TYPE_LABELS) as WalletTransactionType[];
