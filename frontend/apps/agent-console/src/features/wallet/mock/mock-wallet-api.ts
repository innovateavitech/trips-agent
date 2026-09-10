import type {
  StatementFilters,
  StatementPage,
  TopUpIntent,
  TopUpResult,
  WalletSummary,
  WalletTransaction,
  WalletTransactionType,
} from '../types';
import type { WalletApi } from '../wallet-api';

/**
 * ============================================================================
 *  TEMPORARY. Delete this whole `mock/` folder when issue #26 lands.
 * ============================================================================
 *
 * Issue #51 (these screens) depends on #26 (the top-up endpoints), which is not
 * built yet. Rather than block, the screens talk to the `WalletApi` port and
 * this adapter stands in behind it. Swapping in the real client is one line in
 * `main.tsx` — no component changes.
 *
 * It is deliberately a little rude: it takes ~300ms to answer, so loading
 * states are visible during development instead of flashing past on localhost
 * and being discovered as broken on a slow connection in Lagos.
 *
 * State lives in `sessionStorage` because the top-up flow leaves the page
 * entirely (that is the point of a hosted gateway page) and has to still be
 * there when the agent comes back.
 */

const LATENCY_MS = 300;
const STORE_KEY = 'trips.mock.wallet';
const KYB_OVERRIDE_KEY = 'trips.mock.kyb';

const CURRENCY = 'NGN';

/** 2,000,000 naira — a plausible mid-size agency float. */
const OPENING_BALANCE_MINOR = 200_000_000;

interface MockStore {
  balanceMinor: number;
  reservedMinor: number;
  transactions: WalletTransaction[];
  topUps: Record<string, TopUpResult>;
}

const delay = () => new Promise((resolve) => setTimeout(resolve, LATENCY_MS));

/**
 * A tiny deterministic pseudo-random generator (mulberry32).
 *
 * Seeded, so every developer and every reviewer sees the identical statement.
 * `Math.random()` here would mean a screenshot in a PR could never be compared
 * against what the reviewer sees.
 */
function seeded(seed: number): () => number {
  let state = seed;
  return () => {
    state = (state + 0x6d2b79f5) | 0;
    let t = Math.imul(state ^ (state >>> 15), 1 | state);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

const SAMPLE: Array<{ type: WalletTransactionType; description: string; sign: 1 | -1 }> = [
  { type: 'booking_payment', description: 'Flight LOS to ABV - Air Peace P7 122', sign: -1 },
  { type: 'booking_payment', description: 'Flight LOS to LHR - British Airways BA 75', sign: -1 },
  { type: 'booking_payment', description: 'Bus LOS to IBA - GUO Transport', sign: -1 },
  { type: 'hold', description: 'Funds held for order ORD-4471 pending ticketing', sign: -1 },
  {
    type: 'hold_release',
    description: 'Hold released - ticket time limit expired on ORD-4392',
    sign: 1,
  },
  { type: 'refund', description: 'Refund - cancelled booking ORD-4310', sign: 1 },
  { type: 'reversal', description: 'Payment reversed - issuance failed on ORD-4288', sign: 1 },
  { type: 'adjustment', description: 'Goodwill credit applied by Trips support', sign: 1 },
  { type: 'topup', description: 'Wallet top-up - Paystack', sign: 1 },
];

/** Builds 64 rows walking forward in time, so `balanceAfterMinor` is coherent. */
function buildTransactions(): { transactions: WalletTransaction[]; balanceMinor: number } {
  const random = seeded(51);
  const start = Date.UTC(2026, 5, 1, 9, 0, 0);
  let balance = OPENING_BALANCE_MINOR;
  const forward: WalletTransaction[] = [];

  for (let i = 0; i < 64; i += 1) {
    // Non-null assertion: the index is always in range by construction, and
    // TypeScript cannot see that through Math.floor.
    const template = SAMPLE[Math.floor(random() * SAMPLE.length)]!;
    // Whole thousands of naira, so the fixtures read like real fares.
    const magnitude = (5 + Math.floor(random() * 60)) * 100_000;
    const amountMinor = template.sign * magnitude;

    balance += amountMinor;

    forward.push({
      id: `wtx_${String(i + 1).padStart(4, '0')}`,
      occurredAt: new Date(
        start + i * 36 * 60 * 60 * 1000 + Math.floor(random() * 6) * 3_600_000,
      ).toISOString(),
      type: template.type,
      description: template.description,
      amountMinor,
      balanceAfterMinor: balance,
      reference: template.type === 'adjustment' ? null : `REF-${100_000 + i * 37}`,
    });
  }

  // Newest first - the order a statement is read in.
  return { transactions: forward.reverse(), balanceMinor: balance };
}

function freshStore(): MockStore {
  const { transactions, balanceMinor } = buildTransactions();
  return { balanceMinor, reservedMinor: 12_500_000, transactions, topUps: {} };
}

function writeStore(store: MockStore): void {
  try {
    sessionStorage.setItem(STORE_KEY, JSON.stringify(store));
  } catch {
    // Private browsing, or storage disabled. Losing mock state is not worth
    // throwing over - the mock still works, it just forgets between reloads.
  }
}

function readStore(): MockStore {
  try {
    const raw = sessionStorage.getItem(STORE_KEY);
    if (raw) return JSON.parse(raw) as MockStore;
  } catch {
    // See writeStore.
  }
  const store = freshStore();
  writeStore(store);
  return store;
}

function matchesFilters(transaction: WalletTransaction, filters: StatementFilters): boolean {
  if (filters.types.length > 0 && !filters.types.includes(transaction.type)) return false;

  const day = transaction.occurredAt.slice(0, 10);
  if (filters.from && day < filters.from) return false;
  // `to` is inclusive: an agent asking for "up to 31 March" means that day too.
  if (filters.to && day > filters.to) return false;

  return true;
}

/**
 * The KYB status the mock reports.
 *
 * Overridable from the browser console so a reviewer can see the blocked state
 * without a backend:
 *   sessionStorage.setItem('trips.mock.kyb', 'unverified')
 * then reload.
 */
function mockKybStatus(): WalletSummary['kybStatus'] {
  try {
    const override = sessionStorage.getItem(KYB_OVERRIDE_KEY);
    if (override) return override as WalletSummary['kybStatus'];
  } catch {
    // See writeStore.
  }
  return 'verified';
}

export const mockWalletApi: WalletApi = {
  async getSummary() {
    await delay();
    const store = readStore();

    return {
      walletId: 'wal_mock_0001',
      currency: CURRENCY,
      balanceMinor: store.balanceMinor,
      availableMinor: store.balanceMinor - store.reservedMinor,
      reservedMinor: store.reservedMinor,
      status: 'active',
      lowBalanceThresholdMinor: 25_000_000, // 250,000 naira
      kybStatus: mockKybStatus(),
    };
  },

  async getStatement(filters, page, pageSize) {
    await delay();
    const matching = readStore().transactions.filter((t) => matchesFilters(t, filters));
    const offset = (page - 1) * pageSize;

    return {
      transactions: matching.slice(offset, offset + pageSize),
      page,
      pageSize,
      totalCount: matching.length,
    } satisfies StatementPage;
  },

  async getStatementForExport(filters) {
    await delay();
    return readStore().transactions.filter((t) => matchesFilters(t, filters));
  },

  async startTopUp(amountMinor) {
    await delay();
    const store = readStore();
    const reference = `TOPUP-${Date.now().toString(36).toUpperCase()}`;

    store.topUps[reference] = {
      reference,
      status: 'pending',
      amountMinor,
      currency: CURRENCY,
      creditedAt: null,
      failureReason: null,
    };
    writeStore(store);

    return {
      reference,
      amountMinor,
      // Stands in for Paystack's hosted page. A real one is off-origin; this is
      // an in-app route so the whole flow is walkable with no backend running.
      authorizationUrl: `${window.location.origin}/wallet/top-up/mock-gateway?reference=${reference}`,
    } satisfies TopUpIntent;
  },

  async getTopUp(reference) {
    await delay();
    const found = readStore().topUps[reference];

    // An unknown reference is treated as abandoned rather than as an error:
    // the usual cause is a stale link, and "you did not finish paying" is both
    // true and more useful than "something went wrong".
    if (!found) {
      return {
        reference,
        status: 'abandoned',
        amountMinor: 0,
        currency: CURRENCY,
        creditedAt: null,
        failureReason: null,
      };
    }

    return found;
  },
};

/**
 * What the fake gateway page calls to settle a top-up.
 *
 * This is the half that the real Paystack integration does SERVER-side, after
 * verifying the transaction against Paystack's API. It lives in the mock, and
 * only in the mock, precisely because the browser must never be the thing that
 * decides a wallet was credited.
 */
export function settleMockTopUp(reference: string, outcome: 'success' | 'failure'): void {
  const store = readStore();
  const topUp = store.topUps[reference];
  if (!topUp || topUp.status !== 'pending') return;

  if (outcome === 'failure') {
    topUp.status = 'failed';
    topUp.failureReason = 'Your bank declined the payment. No money left your account.';
    writeStore(store);
    return;
  }

  topUp.status = 'succeeded';
  topUp.creditedAt = new Date().toISOString();
  store.balanceMinor += topUp.amountMinor;
  store.transactions.unshift({
    id: `wtx_${reference}`,
    occurredAt: topUp.creditedAt,
    type: 'topup',
    description: 'Wallet top-up - Paystack',
    amountMinor: topUp.amountMinor,
    balanceAfterMinor: store.balanceMinor,
    reference,
  });
  writeStore(store);
}

/** Marks a top-up abandoned - the agent left the gateway without paying. */
export function abandonMockTopUp(reference: string): void {
  const store = readStore();
  const topUp = store.topUps[reference];
  if (!topUp || topUp.status !== 'pending') return;
  topUp.status = 'abandoned';
  writeStore(store);
}
