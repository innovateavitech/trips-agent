import { formatMoney } from '@trips/utils';

/**
 * ============================================================================
 *  Payouts and disputes — the rules, with no React in them (build plan F12).
 * ============================================================================
 *
 * The server enforces every one of these. They live here too so an agent is
 * told "the smallest withdrawal is ₦5,000" before they submit, not after.
 */

export interface WithdrawableBalance {
  balanceMinor: number;
  reservedMinor: number;
  availableMinor: number;
  /** Card money paid in too recently to have reached our bank. */
  pendingSettlementMinor: number;
  /** What may actually leave right now. */
  withdrawableMinor: number;
  minimumPayoutMinor: number;
  dailyCapMinor: number;
  settlementWindowDays: number;
  currency: string;
}

export type AmountParseResult = { ok: true; amountMinor: number } | { ok: false; error: string };

/**
 * Turns what was typed into kobo without ever making a float of it.
 *
 * Split on the decimal point and multiplied as integers: `parseFloat('0.29') * 100`
 * is 28.999999999999996, and a withdrawal a kobo short is a support ticket.
 */
export function parsePayoutAmount(input: string, balance: WithdrawableBalance): AmountParseResult {
  const cleaned = input.trim().replace(/[\s,₦]/g, '');

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

  if (amountMinor < balance.minimumPayoutMinor) {
    return {
      ok: false,
      error: `The smallest withdrawal is ${formatMoney(balance.minimumPayoutMinor, balance.currency)}.`,
    };
  }

  if (amountMinor > balance.withdrawableMinor) {
    return {
      ok: false,
      error: `You can withdraw up to ${formatMoney(balance.withdrawableMinor, balance.currency)} right now.`,
    };
  }

  return { ok: true, amountMinor };
}

/** A NUBAN is ten digits, and nothing else. */
export function isNuban(input: string): boolean {
  return /^\d{10}$/.test(input.trim());
}

export type Tone = 'neutral' | 'primary' | 'success' | 'warning' | 'destructive' | 'info';

export interface StatusCopy {
  label: string;
  tone: Tone;
  /** One line an agent can act on, for the states that need one. */
  explanation?: string;
}

export const PAYOUT_STATUS: Record<string, StatusCopy> = {
  Requested: {
    label: 'Awaiting approval',
    tone: 'info',
    explanation: 'Our finance team checks every withdrawal before it is sent.',
  },
  Approved: { label: 'Approved', tone: 'primary', explanation: 'It will be sent within minutes.' },
  Sending: { label: 'Sending', tone: 'primary' },
  OutcomeUnknown: {
    label: 'Confirming with the bank',
    tone: 'warning',
    explanation:
      'The payment provider has not confirmed this yet. We are checking — it will not be sent twice.',
  },
  Paid: { label: 'Paid', tone: 'success' },
  Rejected: {
    label: 'Declined',
    tone: 'destructive',
    explanation: 'The money is back in your wallet.',
  },
  Failed: {
    label: 'Did not go through',
    tone: 'destructive',
    explanation: 'The money is back in your wallet.',
  },
  Reversed: {
    label: 'Returned by the bank',
    tone: 'destructive',
    explanation: 'The bank sent it back, and it is in your wallet again.',
  },
};

export const BANK_ACCOUNT_STATUS: Record<string, StatusCopy> = {
  PendingVerification: {
    label: 'Checking with your bank',
    tone: 'warning',
    explanation: 'We could not reach the bank. Try adding it again in a minute.',
  },
  Verified: { label: 'Verified', tone: 'success' },
  Rejected: {
    label: 'Not recognised',
    tone: 'destructive',
    explanation: 'Your bank does not recognise this account. Check the number and the bank.',
  },
  Removed: { label: 'Removed', tone: 'neutral' },
};

export const DISPUTE_STATUS: Record<string, StatusCopy> = {
  Open: { label: 'Evidence needed', tone: 'warning' },
  EvidenceSubmitted: { label: 'With the bank', tone: 'info' },
  Expired: { label: 'Deadline missed', tone: 'destructive' },
  Won: { label: 'Won', tone: 'success' },
  Lost: { label: 'Lost', tone: 'destructive' },
};

export function statusCopy(table: Record<string, StatusCopy>, status: string): StatusCopy {
  return table[status] ?? { label: status, tone: 'neutral' };
}

/**
 * Whether a newly added account can receive money yet, and if not, until when.
 * The cooling-off period is a security control, so the screen says why.
 */
export function coolingOff(usableFrom: string | null | undefined, now: Date): string | null {
  if (!usableFrom) {
    return null;
  }

  const from = new Date(usableFrom);
  if (from.getTime() <= now.getTime()) {
    return null;
  }

  return `Can receive withdrawals from ${from.toLocaleString('en-NG', {
    dateStyle: 'medium',
    timeStyle: 'short',
  })}. The wait gives you time to spot a change you did not make.`;
}

/** How loudly to show a dispute's deadline. */
export function deadlineTone(dueAt: string, now: Date): Tone {
  const hoursLeft = (new Date(dueAt).getTime() - now.getTime()) / 3_600_000;

  if (hoursLeft <= 0) {
    return 'destructive';
  }

  return hoursLeft <= 48 ? 'warning' : 'neutral';
}

/** "2 days left", "5 hours left", or "passed". */
export function timeLeft(dueAt: string, now: Date): string {
  const hours = Math.floor((new Date(dueAt).getTime() - now.getTime()) / 3_600_000);

  if (hours < 0) {
    return 'Deadline passed';
  }

  if (hours < 48) {
    return hours <= 1 ? 'Less than 2 hours left' : `${hours} hours left`;
  }

  return `${Math.floor(hours / 24)} days left`;
}
