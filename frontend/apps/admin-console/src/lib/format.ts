/**
 * Dates and places, formatted the same way on every screen.
 *
 * `en-NG` so dates read day-first, the way the Lagos operations team writes them — "3 Sept 2026",
 * never the American "9/3/2026" that is a different day.
 */

const DATE_TIME = new Intl.DateTimeFormat('en-NG', {
  day: 'numeric',
  month: 'short',
  year: 'numeric',
  hour: '2-digit',
  minute: '2-digit',
});

const TIME = new Intl.DateTimeFormat('en-NG', { hour: '2-digit', minute: '2-digit' });

export function formatDateTime(iso: string | null): string {
  if (!iso) return 'Not recorded';
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? 'Not recorded' : DATE_TIME.format(date);
}

export function formatTime(epochMs: number): string {
  return TIME.format(new Date(epochMs));
}

const REGIONS =
  typeof Intl.DisplayNames === 'function'
    ? new Intl.DisplayNames(['en'], { type: 'region' })
    : null;

/** "NG" → "Nigeria". Falls back to the code itself rather than showing nothing. */
export function countryName(code: string): string {
  try {
    return REGIONS?.of(code.toUpperCase()) ?? code;
  } catch {
    return code;
  }
}

const MONEY = new Intl.NumberFormat('en-NG', {
  style: 'currency',
  currency: 'NGN',
  minimumFractionDigits: 2,
});

/**
 * Kobo to naira: `150000` → "₦1,500.00".
 *
 * The API only ever sends money as a whole number of minor units (CLAUDE.md rule 2), and the
 * division to naira happens here, once, at the last possible moment before it is read by a person.
 * Nothing downstream of this function is arithmetic.
 */
export function formatMoney(amountMinor: number | null | undefined, currency = 'NGN'): string {
  if (amountMinor === null || amountMinor === undefined) return '—';

  const formatter =
    currency === 'NGN'
      ? MONEY
      : new Intl.NumberFormat('en-NG', { style: 'currency', currency, minimumFractionDigits: 2 });

  return formatter.format(amountMinor / 100);
}

const RELATIVE = new Intl.RelativeTimeFormat('en', { numeric: 'auto' });

/** "3 days ago", for a column where the exact minute does not matter. */
export function formatRelative(iso: string | null, now: number): string {
  if (!iso) return 'Never';

  const at = new Date(iso).getTime();
  if (Number.isNaN(at)) return 'Never';

  const seconds = Math.round((at - now) / 1000);
  const units: [Intl.RelativeTimeFormatUnit, number][] = [
    ['year', 31_536_000],
    ['month', 2_592_000],
    ['day', 86_400],
    ['hour', 3_600],
    ['minute', 60],
  ];

  for (const [unit, size] of units) {
    if (Math.abs(seconds) >= size) return RELATIVE.format(Math.round(seconds / size), unit);
  }

  return 'just now';
}

/** 750 basis points → "7.5%". Integers in, no decimal arithmetic anywhere near a rate. */
export function formatBasisPoints(basisPoints: number): string {
  return `${(basisPoints / 100).toFixed(basisPoints % 100 === 0 ? 0 : 1)}%`;
}
