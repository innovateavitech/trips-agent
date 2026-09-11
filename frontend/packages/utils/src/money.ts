/**
 * Money is ALWAYS stored and transmitted as an integer number of minor units
 * (kobo for NGN). ₦1,500.00 is 150000. Never use floating point for money —
 * see CLAUDE.md rule 2.
 *
 * These helpers are the only place a minor-unit value should become a number
 * with a decimal point, and that is purely for display.
 */

export type MinorUnits = number;

/** Formats minor units for display. `formatMoney(150000, 'NGN')` → "₦1,500.00" */
export function formatMoney(amountMinor: MinorUnits, currency: string, locale = 'en-NG'): string {
  return new Intl.NumberFormat(locale, {
    style: 'currency',
    currency,
    minimumFractionDigits: 2,
  }).format(amountMinor / 100);
}

/**
 * Like `formatMoney`, but leaves off `.00` when there are no kobo — for prices
 * in lists, where "₦128,450.00" on every row is noise. Amounts with kobo keep
 * both digits. `formatMoneyShort(12845000, 'NGN')` → "₦128,450".
 */
export function formatMoneyShort(
  amountMinor: MinorUnits,
  currency: string,
  locale = 'en-NG',
): string {
  const digits = amountMinor % 100 === 0 ? 0 : 2;
  return new Intl.NumberFormat(locale, {
    style: 'currency',
    currency,
    minimumFractionDigits: digits,
    maximumFractionDigits: digits,
  }).format(amountMinor / 100);
}

/** Parses user input into minor units. `toMinorUnits('1500.50')` → 150050 */
export function toMinorUnits(input: string | number): MinorUnits {
  const value = typeof input === 'number' ? input : Number.parseFloat(input);
  if (!Number.isFinite(value)) {
    throw new Error(`Cannot parse "${input}" as a monetary amount`);
  }
  return Math.round(value * 100);
}
