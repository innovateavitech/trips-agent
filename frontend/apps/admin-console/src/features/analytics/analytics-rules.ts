import type { AnalyticsWindow } from './types';

/**
 * The windows the platform dashboard offers, and the arithmetic on it.
 *
 * The only subtle thing here is the day. The aggregates are keyed by a Lagos calendar day — the
 * day an agent means — so "today" on this screen has to be the Lagos day too, or for an hour
 * either side of midnight the console asks for a day the server does not think has started.
 *
 * West Africa Time is UTC+1 all year with no daylight saving, so shifting the instant by an hour
 * and reading the date off it is exact rather than approximate.
 */

export interface WindowPreset {
  id: string;
  label: string;
  days: number;
}

export const WINDOW_PRESETS: readonly WindowPreset[] = [
  { id: '7', label: '7 days', days: 7 },
  { id: '30', label: '30 days', days: 30 },
  { id: '90', label: '90 days', days: 90 },
  { id: '365', label: '12 months', days: 365 },
];

export const DEFAULT_WINDOW_ID = '30';

export function lagosDay(instant: Date): string {
  return new Date(instant.getTime() + 3_600_000).toISOString().slice(0, 10);
}

export function windowOf(days: number, now: Date = new Date()): AnalyticsWindow {
  return {
    from: lagosDay(new Date(now.getTime() - (days - 1) * 86_400_000)),
    to: lagosDay(now),
  };
}

/**
 * Basis points as a percentage.
 *
 * Null means the question does not apply — no previous window, or no searches to convert — and
 * the caller must say so rather than print `0%`, which is a different and wrong claim.
 */
export function formatBasisPoints(basisPoints: number | null, signed = false): string {
  if (basisPoints === null) return '—';

  const sign = signed && basisPoints > 0 ? '+' : '';
  return `${sign}${(basisPoints / 100).toFixed(1)}%`;
}

export function changeTone(basisPoints: number | null): 'up' | 'down' | 'flat' {
  if (basisPoints === null || basisPoints === 0) return 'flat';
  return basisPoints > 0 ? 'up' : 'down';
}

/**
 * How worried to be about a supplier's error rate.
 *
 * Thresholds rather than a gradient, because the question a person is asking is "do I need to call
 * Trips Africa today". Five per cent of calls failing is a bad afternoon; fifteen is an incident.
 */
export function errorTone(basisPoints: number | null): 'calm' | 'attention' | 'urgent' {
  if (basisPoints === null) return 'calm';
  if (basisPoints >= 1_500) return 'urgent';
  if (basisPoints >= 500) return 'attention';
  return 'calm';
}

/** A day as `9 Mar`, parsed as UTC because it is a calendar day and not an instant. */
export function formatDay(day: string, withYear = false): string {
  const [year, month, date] = day.split('-').map(Number);
  const parsed = new Date(Date.UTC(year ?? 1970, (month ?? 1) - 1, date ?? 1));

  return parsed.toLocaleDateString('en-NG', {
    day: 'numeric',
    month: 'short',
    ...(withYear ? { year: 'numeric' } : {}),
    timeZone: 'UTC',
  });
}

export function spansYears(window: AnalyticsWindow): boolean {
  return window.from.slice(0, 4) !== window.to.slice(0, 4);
}
