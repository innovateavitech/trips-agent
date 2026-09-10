import { beforeEach, describe, expect, it, vi } from 'vitest';
import { abandonMockTopUp, mockWalletApi, settleMockTopUp } from './mock-wallet-api';
import { toStatementCsv } from '../statement-csv';
import type { StatementFilters } from '../types';

/**
 * Delete this with the rest of `mock/` when issue #26 lands.
 *
 * It is a test of the stand-in, not of the product — but it pins down the
 * behaviour the screens were built against, so if the real API disagrees on
 * any of it, that disagreement is a deliberate decision rather than a surprise
 * discovered in a wallet balance.
 */

const storage = new Map<string, string>();

// Installed unconditionally so the test behaves the same under `node` and
// `jsdom`, and so one test cannot leak state into the next.
vi.stubGlobal('sessionStorage', {
  getItem: (key: string) => storage.get(key) ?? null,
  setItem: (key: string, value: string) => void storage.set(key, value),
  removeItem: (key: string) => void storage.delete(key),
  clear: () => storage.clear(),
});

// Only under `node`, where there is no window to borrow an origin from.
if (typeof window === 'undefined') {
  vi.stubGlobal('window', { location: { origin: 'http://localhost:5173' } });
}

const ALL: StatementFilters = { types: [], from: null, to: null };

describe('the stand-in wallet API', () => {
  beforeEach(() => storage.clear());

  it('pages the statement without repeating rows', async () => {
    const everything = await mockWalletApi.getStatementForExport({ ...ALL });
    const first = await mockWalletApi.getStatement({ ...ALL }, 1, 20);
    const second = await mockWalletApi.getStatement({ ...ALL }, 2, 20);

    expect(first.totalCount).toBe(everything.length);
    expect(first.transactions).toHaveLength(20);
    expect(first.transactions[0]!.id).not.toBe(second.transactions[0]!.id);
  });

  it('keeps every running balance consistent with the movement before it', async () => {
    const oldestFirst = (await mockWalletApi.getStatementForExport({ ...ALL })).reverse();

    for (let i = 1; i < oldestFirst.length; i += 1) {
      expect(oldestFirst[i]!.balanceAfterMinor).toBe(
        oldestFirst[i - 1]!.balanceAfterMinor + oldestFirst[i]!.amountMinor,
      );
    }
  });

  it('filters by type', async () => {
    const page = await mockWalletApi.getStatement({ ...ALL, types: ['topup'] }, 1, 100);
    expect(page.totalCount).toBeGreaterThan(0);
    expect(page.transactions.every((t) => t.type === 'topup')).toBe(true);
  });

  it('filters by an inclusive date range', async () => {
    const page = await mockWalletApi.getStatement(
      { types: [], from: '2026-06-01', to: '2026-06-30' },
      1,
      100,
    );
    expect(page.totalCount).toBeGreaterThan(0);
    expect(page.transactions.every((t) => t.occurredAt.slice(0, 7) === '2026-06')).toBe(true);
  });

  it('credits nothing until the payment is settled server-side', async () => {
    const before = await mockWalletApi.getSummary();
    const intent = await mockWalletApi.startTopUp(5_000_000);

    expect((await mockWalletApi.getTopUp(intent.reference)).status).toBe('pending');
    expect((await mockWalletApi.getSummary()).balanceMinor).toBe(before.balanceMinor);

    settleMockTopUp(intent.reference, 'success');

    const after = await mockWalletApi.getSummary();
    expect(after.balanceMinor).toBe(before.balanceMinor + 5_000_000);
    expect((await mockWalletApi.getTopUp(intent.reference)).status).toBe('succeeded');
  });

  it('puts a settled top-up at the top of the statement with the new balance', async () => {
    const intent = await mockWalletApi.startTopUp(5_000_000);
    settleMockTopUp(intent.reference, 'success');

    const summary = await mockWalletApi.getSummary();
    const page = await mockWalletApi.getStatement({ ...ALL, types: ['topup'] }, 1, 5);

    expect(page.transactions[0]!.reference).toBe(intent.reference);
    expect(page.transactions[0]!.balanceAfterMinor).toBe(summary.balanceMinor);
  });

  it('leaves the balance untouched when a payment fails or is abandoned', async () => {
    const before = await mockWalletApi.getSummary();

    const declined = await mockWalletApi.startTopUp(5_000_000);
    settleMockTopUp(declined.reference, 'failure');
    expect((await mockWalletApi.getTopUp(declined.reference)).status).toBe('failed');

    const walkedAway = await mockWalletApi.startTopUp(5_000_000);
    abandonMockTopUp(walkedAway.reference);
    expect((await mockWalletApi.getTopUp(walkedAway.reference)).status).toBe('abandoned');

    expect((await mockWalletApi.getSummary()).balanceMinor).toBe(before.balanceMinor);
  });

  it('does not credit the same top-up twice', async () => {
    // The duplicate-webhook case from issue #26, checked at the boundary the
    // screens can actually see.
    const before = await mockWalletApi.getSummary();
    const intent = await mockWalletApi.startTopUp(5_000_000);

    settleMockTopUp(intent.reference, 'success');
    settleMockTopUp(intent.reference, 'success');

    expect((await mockWalletApi.getSummary()).balanceMinor).toBe(before.balanceMinor + 5_000_000);
  });

  it('treats an unknown reference as abandoned rather than as an error', async () => {
    expect((await mockWalletApi.getTopUp('TOPUP-NOPE')).status).toBe('abandoned');
  });

  it('exports every matching row, not only the visible page', async () => {
    const rows = await mockWalletApi.getStatementForExport({ ...ALL });
    expect(rows.length).toBeGreaterThan(20);
    expect(toStatementCsv(rows, 'NGN').trimEnd().split('\r\n')).toHaveLength(rows.length + 1);
  });
});
