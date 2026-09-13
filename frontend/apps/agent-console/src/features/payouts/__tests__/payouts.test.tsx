// @vitest-environment jsdom
import { cleanup, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it } from 'vitest';
import { BalanceBreakdown } from '../pages/payouts-page';
import { DisputeTable } from '../pages/disputes-page';
import type { Dispute } from '../payout-queries';
import {
  coolingOff,
  deadlineTone,
  isNuban,
  parsePayoutAmount,
  PAYOUT_STATUS,
  statusCopy,
  timeLeft,
  type WithdrawableBalance,
} from '../payout-rules';

afterEach(cleanup);

const balance: WithdrawableBalance = {
  balanceMinor: 1_000_000,
  reservedMinor: 200_000,
  availableMinor: 800_000,
  pendingSettlementMinor: 300_000,
  withdrawableMinor: 500_000,
  minimumPayoutMinor: 500_000,
  dailyCapMinor: 500_000_000,
  settlementWindowDays: 2,
  currency: 'NGN',
};

describe('parsePayoutAmount', () => {
  it('turns naira and kobo into minor units without floating point', () => {
    expect(parsePayoutAmount('5,000.29', { ...balance, withdrawableMinor: 900_000 })).toEqual({
      ok: true,
      amountMinor: 500_029,
    });
    expect(parsePayoutAmount('5000', balance)).toEqual({ ok: true, amountMinor: 500_000 });
  });

  it('refuses below the minimum and above what is withdrawable', () => {
    expect(parsePayoutAmount('4999.99', balance).ok).toBe(false);
    const tooMuch = parsePayoutAmount('5000.01', balance);
    expect(tooMuch.ok).toBe(false);
    expect(tooMuch.ok ? '' : tooMuch.error).toContain('up to');
  });

  it('refuses anything that is not a plain amount', () => {
    expect(parsePayoutAmount('', balance).ok).toBe(false);
    expect(parsePayoutAmount('5000.123', balance).ok).toBe(false);
    expect(parsePayoutAmount('-5000', balance).ok).toBe(false);
  });
});

describe('payout rules', () => {
  it('knows a NUBAN is ten digits', () => {
    expect(isNuban('0123456789')).toBe(true);
    expect(isNuban('012345678')).toBe(false);
    expect(isNuban('01234567890')).toBe(false);
  });

  it('explains an unknown outcome as being checked, never as failed', () => {
    const copy = statusCopy(PAYOUT_STATUS, 'OutcomeUnknown');
    expect(copy.tone).toBe('warning');
    expect(copy.explanation).toContain('will not be sent twice');
  });

  it('says when a new account can receive money, and nothing once it can', () => {
    const now = new Date('2026-09-12T10:00:00Z');
    expect(coolingOff('2026-09-13T10:00:00Z', now)).toContain('Can receive withdrawals from');
    expect(coolingOff('2026-09-11T10:00:00Z', now)).toBeNull();
  });

  it('gets louder as a dispute deadline nears', () => {
    const now = new Date('2026-09-12T10:00:00Z');
    expect(deadlineTone('2026-09-20T10:00:00Z', now)).toBe('neutral');
    expect(deadlineTone('2026-09-13T10:00:00Z', now)).toBe('warning');
    expect(deadlineTone('2026-09-12T09:00:00Z', now)).toBe('destructive');
    expect(timeLeft('2026-09-13T10:00:00Z', now)).toBe('24 hours left');
  });
});

describe('screens', () => {
  it('shows withdrawable as distinct from balance, with the reasons', () => {
    render(<BalanceBreakdown balance={balance} />);

    expect(screen.getByText('Available to withdraw')).toBeTruthy();
    expect(screen.getByText('Held for bookings in progress')).toBeTruthy();
    expect(screen.getByText(/not yet cleared/)).toBeTruthy();
  });

  it('lists disputes with how long is left to answer', () => {
    const dispute: Dispute = {
      id: 'd1',
      paymentReference: 'TU-123',
      amountMinor: 400_000,
      currency: 'NGN',
      category: 'chargeback',
      reason: null,
      status: 'Open',
      holdOutcome: 'Held',
      openedAt: '2026-09-12T08:00:00Z',
      evidenceDueAt: '2026-09-13T10:00:00Z',
      evidenceSubmittedAt: null,
      resolvedAt: null,
      acceptsEvidence: true,
      evidenceDefaults: null,
    };

    render(
      <MemoryRouter>
        <DisputeTable disputes={[dispute]} now={new Date('2026-09-12T10:00:00Z')} />
      </MemoryRouter>,
    );

    expect(screen.getByText('TU-123')).toBeTruthy();
    expect(screen.getByText('Evidence needed')).toBeTruthy();
    expect(screen.getByText('24 hours left')).toBeTruthy();
  });
});
