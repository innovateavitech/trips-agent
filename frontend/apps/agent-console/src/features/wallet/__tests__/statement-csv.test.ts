import { describe, expect, it } from 'vitest';
import { minorToPlainDecimal, statementFileName, toStatementCsv } from '../statement-csv';
import type { StatementFilters, WalletTransaction } from '../types';

const row = (overrides: Partial<WalletTransaction> = {}): WalletTransaction => ({
  id: 'wtx_1',
  occurredAt: '2026-03-14T09:30:00.000Z',
  type: 'topup',
  description: 'Wallet top-up - Paystack',
  amountMinor: 5_000_000,
  balanceAfterMinor: 55_000_000,
  reference: 'REF-1001',
  ...overrides,
});

describe('minorToPlainDecimal', () => {
  it.each([
    [0, '0.00'],
    [5, '0.05'],
    [50, '0.50'],
    [100, '1.00'],
    [150_000, '1500.00'],
    [150_050, '1500.50'],
    [-150_050, '-1500.50'],
  ])('renders %d minor units as %s', (minor, expected) => {
    expect(minorToPlainDecimal(minor)).toBe(expected);
  });

  it('does not group thousands', () => {
    // A spreadsheet reads "1,500.00" as text, not a number, which silently
    // breaks every SUM the agent's accountant writes over the column.
    expect(minorToPlainDecimal(200_000_000)).toBe('2000000.00');
  });
});

describe('toStatementCsv', () => {
  it('names the currency in the money column headers', () => {
    const [header] = toStatementCsv([], 'NGN').split('\r\n');
    expect(header).toContain('"Amount (NGN)"');
    expect(header).toContain('"Balance after (NGN)"');
  });

  it('writes one line per transaction, CRLF-terminated', () => {
    const csv = toStatementCsv([row(), row({ id: 'wtx_2' })], 'NGN');
    expect(csv.endsWith('\r\n')).toBe(true);
    expect(csv.trimEnd().split('\r\n')).toHaveLength(3); // header + two rows
  });

  it('uses the display label for the type', () => {
    expect(toStatementCsv([row({ type: 'hold_release' })], 'NGN')).toContain('"Hold released"');
  });

  it('writes an empty reference rather than the word null', () => {
    expect(toStatementCsv([row({ reference: null })], 'NGN')).toContain('"",');
  });

  it('escapes embedded quotes by doubling them', () => {
    const csv = toStatementCsv([row({ description: 'Booking "URGENT"' })], 'NGN');
    expect(csv).toContain('"Booking ""URGENT"""');
  });

  it('keeps a comma inside a field from splitting the row', () => {
    const csv = toStatementCsv([row({ description: 'Lagos, Nigeria' })], 'NGN');
    expect(csv).toContain('"Lagos, Nigeria"');
  });

  it('neutralises a description that a spreadsheet would run as a formula', () => {
    // Excel treats a leading = as a formula. The description is supplied
    // upstream by agents and travellers, so it is the field to worry about.
    const csv = toStatementCsv([row({ description: '=1+1' })], 'NGN');
    expect(csv).toContain(`"'=1+1"`);
  });

  it('leaves a negative amount readable as a number, not as a formula', () => {
    // A leading minus IS guarded in text fields, but the amount column has to
    // stay numeric or the export is useless.
    const csv = toStatementCsv([row({ amountMinor: -150_000 })], 'NGN');
    expect(csv).toContain('"-1500.00"');
  });
});

describe('statementFileName', () => {
  const today = new Date('2026-09-10T00:00:00.000Z');

  it('names both ends of an explicit range', () => {
    const filters: StatementFilters = { types: [], from: '2026-01-01', to: '2026-03-31' };
    expect(statementFileName(filters, today)).toBe('wallet-statement-2026-01-01-to-2026-03-31.csv');
  });

  it('falls back to today when there is no end date', () => {
    const filters: StatementFilters = { types: [], from: '2026-01-01', to: null };
    expect(statementFileName(filters, today)).toBe('wallet-statement-2026-01-01-to-2026-09-10.csv');
  });

  it('says only the end date when the range is open at the start', () => {
    const filters: StatementFilters = { types: [], from: null, to: null };
    expect(statementFileName(filters, today)).toBe('wallet-statement-to-2026-09-10.csv');
  });
});
