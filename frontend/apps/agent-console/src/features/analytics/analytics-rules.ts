import type { AnalyticsWindow } from './types';

/**
 * The windows the screen offers, and the arithmetic behind the numbers on it.
 *
 * Everything here is pure and unit-tested. The interesting parts are the two
 * places a naive implementation quietly lies: a change from a previous window
 * of zero, and a percentage rendered from a floating-point division.
 */

export interface WindowPreset {
  id: string;
  label: string;
  days: number;
}

/**
 * Deliberately short. Thirty days is what a dashboard opens on; ninety is the
 * longest a report still comes back in the request, so it is a useful edge to
 * be able to reach in one click.
 */
export const WINDOW_PRESETS: readonly WindowPreset[] = [
  { id: '7', label: 'Last 7 days', days: 7 },
  { id: '30', label: 'Last 30 days', days: 30 },
  { id: '90', label: 'Last 90 days', days: 90 },
  { id: '365', label: 'Last 12 months', days: 365 },
];

export const DEFAULT_WINDOW_ID = '30';

/**
 * A window ending today, in Lagos days.
 *
 * The aggregates are keyed by a Lagos calendar day, so "today" here has to be
 * the Lagos day too — otherwise for an hour either side of midnight the screen
 * asks for a day the server does not think has started yet.
 *
 * West Africa Time is UTC+1 all year, with no daylight saving, so adding an
 * hour to the UTC instant and reading the date off it is exact.
 */
export function windowOf(days: number, now: Date = new Date()): AnalyticsWindow {
  const to = lagosDay(now);
  const from = lagosDay(new Date(now.getTime() - (days - 1) * 86_400_000));
  return { from, to };
}

/** The Lagos calendar day an instant falls on, as `YYYY-MM-DD`. */
export function lagosDay(instant: Date): string {
  const shifted = new Date(instant.getTime() + 3_600_000);
  return shifted.toISOString().slice(0, 10);
}

/**
 * Basis points as a percentage a person reads: `1250` becomes `+12.5%`.
 *
 * Null means there is nothing to compare against — the previous window sold
 * nothing — and the caller should say so rather than print `+0%`, which reads
 * as "flat" and is a different claim entirely.
 */
export function formatChange(basisPoints: number | null): string | null {
  if (basisPoints === null) return null;

  const sign = basisPoints > 0 ? '+' : '';
  return `${sign}${(basisPoints / 100).toFixed(1)}%`;
}

/** Whether a change should be drawn as good, bad or neutral. */
export function changeTone(basisPoints: number | null): 'up' | 'down' | 'flat' {
  if (basisPoints === null || basisPoints === 0) return 'flat';
  return basisPoints > 0 ? 'up' : 'down';
}

/** A ratio of two counts as a percentage, or an em dash when there is no denominator. */
export function formatRatio(numerator: number, denominator: number): string {
  if (denominator === 0) return '—';
  return `${((numerator / denominator) * 100).toFixed(1)}%`;
}

/**
 * A day as `9 Mar`, or `9 Mar 2026` when the window crosses a year boundary.
 *
 * Parsed as UTC on purpose: the string is a calendar day, not an instant, and
 * `new Date('2026-03-09')` in a browser west of Greenwich would otherwise draw
 * it as the 8th.
 */
export function formatDay(day: string, withYear = false): string {
  const [year, month, date] = day.split('-').map(Number);
  const parsed = new Date(Date.UTC(year ?? 1970, (month ?? 1) - 1, date ?? 1));

  return parsed.toLocaleDateString('en-GB', {
    day: 'numeric',
    month: 'short',
    ...(withYear ? { year: 'numeric' } : {}),
    timeZone: 'UTC',
  });
}

/** Whether a window spans more than one calendar year, so days need their year shown. */
export function spansYears(window: AnalyticsWindow): boolean {
  return window.from.slice(0, 4) !== window.to.slice(0, 4);
}

/**
 * How a report of this shape will run, before anybody presses anything.
 *
 * The same rule the server applies (`ReportScopeRules`), restated here only so
 * the button can say "this will be emailed to you" instead of the agent finding
 * out afterwards. The server decides; this predicts.
 */
export function willBeQueued(
  definition: { alwaysAsynchronous: boolean; synchronousDayLimit: number },
  window: AnalyticsWindow,
): boolean {
  return definition.alwaysAsynchronous || daysCovered(window) > definition.synchronousDayLimit;
}

/** Days in an inclusive window. One day is one day, not zero. */
export function daysCovered(window: AnalyticsWindow): number {
  const from = Date.parse(`${window.from}T00:00:00Z`);
  const to = Date.parse(`${window.to}T00:00:00Z`);

  if (Number.isNaN(from) || Number.isNaN(to) || to < from) return 0;

  return Math.round((to - from) / 86_400_000) + 1;
}

/** A file size a person reads. Reports are text, so this rarely leaves kilobytes. */
export function formatSize(bytes: number | null): string {
  if (bytes === null) return '—';
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}
