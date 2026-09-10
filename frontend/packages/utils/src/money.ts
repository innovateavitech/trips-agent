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

/** Parses user input into minor units. `toMinorUnits('1500.50')` → 150050 */
export function toMinorUnits(input: string | number): MinorUnits {
  const value = typeof input === 'number' ? input : Number.parseFloat(input);
  if (!Number.isFinite(value)) {
    throw new Error(`Cannot parse "${input}" as a monetary amount`);
  }
  return Math.round(value * 100);
}
