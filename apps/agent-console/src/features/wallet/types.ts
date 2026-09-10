/**
 * The shapes the wallet screens read.
 *
 * These are hand-written for now. When issue #26 lands the real endpoints and
 * `pnpm generate:api` produces `@trips/api-client` from the C# DTOs, delete this
 * file and import from there instead — generated types are the single source of
 * truth, and two hand-maintained copies of a money shape is how a field ends up
 * meaning kobo on one side and naira on the other.
 *
 * Every `*Minor` field is an INTEGER number of minor units (kobo for NGN).
 * ₦1,500.00 is 150000. See CLAUDE.md rule 2.
 */

/** ISO 4217, e.g. `'NGN'`. An agency has exactly one wallet per currency. */
export type Currency = string;

/** Where the agency sits in KYB. Only `verified` may move money. */
export type KybStatus = 'unverified' | 'pending' | 'verified' | 'rejected' | 'suspended';

export type WalletStatus = 'active' | 'frozen';

/**
 * The kinds of line an agent sees on their statement. This is the agent-facing
 * projection (`wallet_transactions`), not the double-entry ledger underneath —
 * the ledger has two sides per movement, the statement has one row.
 */
export type WalletTransactionType =
  'topup' | 'booking_payment' | 'refund' | 'hold' | 'hold_release' | 'adjustment' | 'reversal';

export interface WalletSummary {
  walletId: string;
  currency: Currency;
  /** Everything in the wallet, including funds reserved against pending bookings. */
  balanceMinor: number;
  /** What can actually be spent right now. `balanceMinor - reservedMinor`. */
  availableMinor: number;
  /** Held against bookings that are confirmed but not yet ticketed. */
  reservedMinor: number;
  status: WalletStatus;
  /** Agent-configured. `null` means they have not set one, so no warning shows. */
  lowBalanceThresholdMinor: number | null;
  kybStatus: KybStatus;
}

export interface WalletTransaction {
  id: string;
  /** ISO 8601, UTC. */
  occurredAt: string;
  type: WalletTransactionType;
  description: string;
  /** Signed: positive credits the wallet, negative debits it. */
  amountMinor: number;
  /**
   * The wallet balance immediately after this movement, as recorded by the
   * server at the time. Never recompute this on the client by adding up the
   * rows on screen — under a filter or on page 3 you would be summing a subset
   * and confidently showing the agent a number that is not their balance.
   */
  balanceAfterMinor: number;
  /** Payment reference, order number, or `null` for an adjustment. */
  reference: string | null;
}

export interface StatementFilters {
  /** Empty means "every type". */
  types: WalletTransactionType[];
  /** `yyyy-mm-dd`, inclusive, or `null` for unbounded. */
  from: string | null;
  to: string | null;
}

export interface StatementPage {
  transactions: WalletTransaction[];
  page: number;
  pageSize: number;
  totalCount: number;
}

/**
 * `pending`   — the agent is at, or has just left, the gateway; outcome unknown.
 * `succeeded` — the SERVER verified the payment and credited the wallet.
 * `failed`    — the gateway declined it.
 * `abandoned` — the agent left without paying, and the intent has expired.
 */
export type TopUpStatus = 'pending' | 'succeeded' | 'failed' | 'abandoned';

export interface TopUpIntent {
  /** Our reference, not the gateway's. Survives the round trip in the return URL. */
  reference: string;
  amountMinor: number;
  /** Paystack's hosted checkout page. We send the whole tab there. */
  authorizationUrl: string;
}

export interface TopUpResult {
  reference: string;
  status: TopUpStatus;
  amountMinor: number;
  currency: Currency;
  /** Set only once the server has verified and credited. */
  creditedAt: string | null;
  /** Human-readable, safe to show. `null` unless `status` is `failed`. */
  failureReason: string | null;
}
