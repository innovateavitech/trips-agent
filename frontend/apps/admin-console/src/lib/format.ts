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
