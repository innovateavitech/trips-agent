import { formatMoney } from '@trips/utils';
import type { Currency, KybStatus, WalletSummary } from './types';

/**
 * The rules that decide whether an agent may top up, and for how much.
 *
 * They live here as plain functions rather than inside the form component so
 * they can be tested without rendering anything, and so the reason a button is
 * disabled is a value you can read rather than a condition buried in JSX.
 *
 * IMPORTANT: none of this is security. The server enforces the same rules in
 * issue #26 and is the only thing that decides whether money moves. This exists
 * so the agent gets told *why* before they waste a trip to the gateway.
 */

/** ₦1,000. Below this the gateway fee eats an absurd share of the top-up. */
export const MIN_TOPUP_MINOR = 100_000;

/** ₦5,000,000 in one go. Larger amounts go through a bank transfer instead. */
export const MAX_TOPUP_MINOR = 500_000_000;

/** Offered as one-tap buttons: ₦50k, ₦100k, ₦250k, ₦500k. */
export const QUICK_TOPUP_AMOUNTS_MINOR = [5_000_000, 10_000_000, 25_000_000, 50_000_000];

export interface TopUpBlock {
  /** Shown as the alert heading. */
  title: string;
  /** Shown as the body. Says what to do next, not just what is wrong. */
  detail: string;
}

const KYB_BLOCKS: Partial<Record<KybStatus, TopUpBlock>> = {
  unverified: {
    title: 'Verify your business before adding funds',
    detail:
      'We have to confirm your business is real before you can hold money with us. Verification usually takes under two working days once your documents are in.',
  },
  pending: {
    title: 'Your verification is still being reviewed',
    detail:
      'We are checking the documents you submitted. You will be able to top up as soon as that is approved — nothing more is needed from you right now.',
  },
  rejected: {
    title: 'Your verification was not approved',
    detail:
      'Top-ups are closed until this is resolved. Check the notes on your verification page, or contact support if you think this is a mistake.',
  },
  suspended: {
    title: 'This account is suspended',
    detail: 'Top-ups are closed while an account is suspended. Please contact support.',
  },
};

/**
 * Why this agent cannot top up, or `null` if they can.
 *
 * A frozen wallet is checked before KYB: if both are true, "your wallet is
 * frozen" is the more actionable of the two messages.
 */
export function topUpBlock(summary: WalletSummary): TopUpBlock | null {
  if (summary.status === 'frozen') {
    return {
      title: 'This wallet is frozen',
      detail:
        'No money can move in or out while a wallet is frozen. Contact support to find out why and what is needed to lift it.',
    };
  }

  return KYB_BLOCKS[summary.kybStatus] ?? null;
}

export type AmountParseResult = { ok: true; amountMinor: number } | { ok: false; error: string };

/**
 * Turns what the agent typed into minor units, or explains why it cannot.
 *
 * Note the regex rather than `parseFloat`: `parseFloat('1500abc')` is 1500,
 * which would silently charge someone for a typo. Two decimal places at most,
 * because a third of a kobo does not exist.
 *
 * The arithmetic is integer-only for the same reason — `50000.10 * 100` is
 * 5000009.999999999 in JavaScript, and rounding that away is exactly the class
 * of bug CLAUDE.md rule 2 exists to prevent.
 */
export function parseTopUpAmount(input: string, currency: Currency): AmountParseResult {
  const cleaned = input.trim().replace(/[\s,]/g, '');

  if (cleaned === '') {
    return { ok: false, error: 'Enter an amount.' };
  }

  if (!/^\d+(\.\d{1,2})?$/.test(cleaned)) {
    return { ok: false, error: 'Enter a plain amount, like 50000 or 50000.00.' };
  }

  const [whole, fraction = ''] = cleaned.split('.');
  const amountMinor = Number(whole) * 100 + Number(fraction.padEnd(2, '0'));

  if (!Number.isSafeInteger(amountMinor)) {
    return { ok: false, error: 'That amount is too large.' };
  }

  if (amountMinor < MIN_TOPUP_MINOR) {
    return {
      ok: false,
      error: `The smallest top-up is ${formatMoney(MIN_TOPUP_MINOR, currency)}.`,
    };
  }

  if (amountMinor > MAX_TOPUP_MINOR) {
    return {
      ok: false,
      error: `The largest single top-up is ${formatMoney(MAX_TOPUP_MINOR, currency)}. For more than that, use a bank transfer.`,
    };
  }

  return { ok: true, amountMinor };
}

/**
 * Is the spendable balance at or below the agent's own warning threshold?
 *
 * Deliberately reads `availableMinor`, not `balanceMinor`. Money reserved
 * against a booking that has not ticketed yet cannot pay for the next one, so
 * a wallet holding ₦2m with ₦1.99m reserved is, for buying purposes, empty.
 */
export function isLowBalance(summary: WalletSummary): boolean {
  if (summary.lowBalanceThresholdMinor === null) return false;
  return summary.availableMinor <= summary.lowBalanceThresholdMinor;
}
