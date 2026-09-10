import { describe, expect, it } from 'vitest';
import {
  MAX_TOPUP_MINOR,
  MIN_TOPUP_MINOR,
  isLowBalance,
  parseTopUpAmount,
  topUpBlock,
} from '../top-up-rules';
import type { WalletSummary } from '../types';

const wallet = (overrides: Partial<WalletSummary> = {}): WalletSummary => ({
  walletId: 'wal_1',
  currency: 'NGN',
  balanceMinor: 50_000_000,
  availableMinor: 50_000_000,
  reservedMinor: 0,
  status: 'active',
  lowBalanceThresholdMinor: null,
  kybStatus: 'verified',
  ...overrides,
});

describe('parseTopUpAmount', () => {
  it('reads a whole-naira amount as minor units', () => {
    expect(parseTopUpAmount('50000', 'NGN')).toEqual({ ok: true, amountMinor: 5_000_000 });
  });

  it('reads kobo', () => {
    expect(parseTopUpAmount('1500.50', 'NGN')).toEqual({ ok: true, amountMinor: 150_050 });
  });

  it('treats a single decimal place as tens of kobo, not units', () => {
    // '1000.5' is 1000 naira 50 kobo. Getting this wrong by padding the other
    // way would undercharge by a factor of ten.
    expect(parseTopUpAmount('1000.5', 'NGN')).toEqual({ ok: true, amountMinor: 100_050 });
  });

  it('ignores thousands separators and stray spaces', () => {
    expect(parseTopUpAmount(' 1,500.00 ', 'NGN')).toEqual({ ok: true, amountMinor: 150_000 });
  });

  it('does not lose a kobo to floating point', () => {
    // 50000.10 * 100 is 5000009.999999999 in IEEE 754. Integer maths must not.
    expect(parseTopUpAmount('50000.10', 'NGN')).toEqual({ ok: true, amountMinor: 5_000_010 });
  });

  it.each(['', '   ', 'abc', '1500abc', '-1500', '1.234', '1e5', '1..5'])('rejects %o', (input) => {
    expect(parseTopUpAmount(input, 'NGN').ok).toBe(false);
  });

  it('rejects an amount below the minimum', () => {
    const result = parseTopUpAmount('999', 'NGN');
    expect(result.ok).toBe(false);
    expect(result.ok === false && result.error).toContain('smallest');
  });

  it('accepts exactly the minimum', () => {
    expect(parseTopUpAmount('1000', 'NGN')).toEqual({ ok: true, amountMinor: MIN_TOPUP_MINOR });
  });

  it('accepts exactly the maximum', () => {
    expect(parseTopUpAmount('5000000', 'NGN')).toEqual({ ok: true, amountMinor: MAX_TOPUP_MINOR });
  });

  it('rejects an amount above the maximum', () => {
    const result = parseTopUpAmount('5000000.01', 'NGN');
    expect(result.ok).toBe(false);
    expect(result.ok === false && result.error).toContain('bank transfer');
  });
});

describe('topUpBlock', () => {
  it('allows a verified agency with an active wallet', () => {
    expect(topUpBlock(wallet())).toBeNull();
  });

  it.each(['unverified', 'pending', 'rejected', 'suspended'] as const)(
    'blocks a %s agency and explains why',
    (kybStatus) => {
      const block = topUpBlock(wallet({ kybStatus }));
      expect(block).not.toBeNull();
      expect(block?.detail.length).toBeGreaterThan(0);
    },
  );

  it('reports a frozen wallet ahead of the KYB status', () => {
    const block = topUpBlock(wallet({ status: 'frozen', kybStatus: 'unverified' }));
    expect(block?.title).toContain('frozen');
  });
});

describe('isLowBalance', () => {
  it('is false when the agent has set no threshold', () => {
    expect(isLowBalance(wallet({ availableMinor: 1, lowBalanceThresholdMinor: null }))).toBe(false);
  });

  it('warns at the threshold, not only below it', () => {
    expect(
      isLowBalance(wallet({ availableMinor: 25_000_000, lowBalanceThresholdMinor: 25_000_000 })),
    ).toBe(true);
  });

  it('does not warn above the threshold', () => {
    expect(
      isLowBalance(wallet({ availableMinor: 25_000_001, lowBalanceThresholdMinor: 25_000_000 })),
    ).toBe(false);
  });

  it('measures available funds, not the total balance', () => {
    // The money is there, but it is spoken for. Booking anything else fails.
    const nearlyAllReserved = wallet({
      balanceMinor: 200_000_000,
      reservedMinor: 199_000_000,
      availableMinor: 1_000_000,
      lowBalanceThresholdMinor: 25_000_000,
    });
    expect(isLowBalance(nearlyAllReserved)).toBe(true);
  });
});
