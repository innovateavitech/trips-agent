/**
 * Dates on the billing screens, in the one format the console uses for them.
 *
 * `en-NG` so they read day-first — "3 September 2026", never the American "9/3/2026", which is a
 * different day. A bill is not a thing to be ambiguous about.
 */
const DATE = new Intl.DateTimeFormat('en-NG', { day: 'numeric', month: 'long', year: 'numeric' });

export function formatDate(iso: string | null): string {
  if (!iso) return 'Not set';

  const date = new Date(iso);

  return Number.isNaN(date.getTime()) ? 'Not set' : DATE.format(date);
}
