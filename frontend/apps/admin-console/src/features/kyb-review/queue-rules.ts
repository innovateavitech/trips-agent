import type { KybQueueItem } from './types';

/**
 * How the queue is ordered, filtered and aged.
 *
 * Plain functions rather than logic inside the table component, so they are testable without
 * rendering, and so "why is this agency first?" has an answer you can read.
 */

export type QueueFilter = 'all' | 'Submitted' | 'UnderReview';

export const QUEUE_FILTERS: ReadonlyArray<{ value: QueueFilter; label: string }> = [
  { value: 'all', label: 'All waiting' },
  { value: 'Submitted', label: 'Submitted' },
  { value: 'UnderReview', label: 'Under review' },
];

/** Reads `?status=` from the address bar. Anything unrecognised shows everything. */
export function parseQueueFilter(value: string | null): QueueFilter {
  return value === 'Submitted' || value === 'UnderReview' ? value : 'all';
}

export interface RankedQueueItem extends KybQueueItem {
  /** 1 is the agency that has waited longest. Stays the same when a filter hides others. */
  position: number;
}

/**
 * Oldest first — a first-in, first-out queue, never a stack.
 *
 * The API already sends this order (KybReviewHandler.QueueAsync). It is applied again here so the
 * rule holds even if the endpoint's ordering ever changes, because it is the rule that matters:
 * an agency that cannot transact loses business every day it waits, so whoever has waited
 * longest goes first. Submissions without a date go last rather than jumping the line.
 */
export function rankQueue(items: readonly KybQueueItem[]): RankedQueueItem[] {
  return [...items]
    .sort((a, b) => {
      const byTime = timestamp(a.submittedAt) - timestamp(b.submittedAt);
      if (byTime !== 0 && !Number.isNaN(byTime)) return byTime;
      return a.submissionId.localeCompare(b.submissionId);
    })
    .map((item, index) => ({ ...item, position: index + 1 }));
}

export function applyQueueFilter<T extends KybQueueItem>(
  items: readonly T[],
  filter: QueueFilter,
): T[] {
  return filter === 'all' ? [...items] : items.filter((item) => item.status === filter);
}

export function countByFilter(items: readonly KybQueueItem[]): Record<QueueFilter, number> {
  return {
    all: items.length,
    Submitted: items.filter((item) => item.status === 'Submitted').length,
    UnderReview: items.filter((item) => item.status === 'UnderReview').length,
  };
}

/**
 * The review target. Past it, the nightly KybReviewSlaMonitor raises an admin alert (plan §3,
 * job 28), so the queue shows the same line the alert is drawn at.
 */
export const REVIEW_TARGET_HOURS = 48;

/** Halfway to the target: time to pick it up today. */
export const WARNING_AFTER_HOURS = 24;

export interface WaitingTime {
  label: string;
  tone: 'neutral' | 'warning' | 'destructive';
  /** Past the review target. */
  overdue: boolean;
}

const HOUR_MS = 3_600_000;

export function waitingTime(submittedAt: string | null, now: number): WaitingTime {
  const submitted = submittedAt === null ? Number.NaN : Date.parse(submittedAt);
  if (Number.isNaN(submitted)) return { label: 'Unknown', tone: 'neutral', overdue: false };

  const waitedMs = Math.max(0, now - submitted);
  const hours = waitedMs / HOUR_MS;

  return {
    label: describeDuration(waitedMs),
    tone:
      hours >= REVIEW_TARGET_HOURS
        ? 'destructive'
        : hours >= WARNING_AFTER_HOURS
          ? 'warning'
          : 'neutral',
    overdue: hours >= REVIEW_TARGET_HOURS,
  };
}

/**
 * "Under an hour", "5 hours", "1 day 6 hours", "9 days".
 *
 * Hours are kept alongside days for the first week, because "1 day" hides whether the agency is
 * one hour or twenty-three hours from the 48-hour target.
 */
export function describeDuration(ms: number): string {
  const totalHours = Math.floor(Math.max(0, ms) / HOUR_MS);
  if (totalHours < 1) return 'Under an hour';
  if (totalHours < 24) return plural(totalHours, 'hour');

  const days = Math.floor(totalHours / 24);
  const hours = totalHours % 24;
  if (days < 7 && hours > 0) return `${plural(days, 'day')} ${plural(hours, 'hour')}`;
  return plural(days, 'day');
}

/**
 * The submission to offer after a decision: the oldest one that is not the one just decided.
 *
 * Excludes the current submission explicitly rather than trusting the refetched queue to have
 * dropped it — the refetch may not have landed yet, and "review next" must never lead back to
 * the agency the reviewer has just finished with.
 */
export function nextInQueue(
  items: readonly KybQueueItem[],
  currentSubmissionId: string,
): RankedQueueItem | null {
  return rankQueue(items).find((item) => item.submissionId !== currentSubmissionId) ?? null;
}

/** What to say when a filter hides everything. */
export function emptyFilterCopy(filter: Exclude<QueueFilter, 'all'>): {
  title: string;
  detail: string;
} {
  return filter === 'UnderReview'
    ? {
        title: 'Nothing is marked under review',
        detail:
          'The console cannot mark a submission as picked up yet, so everything waiting shows as Submitted.',
      }
    : {
        title: 'Nothing is waiting as Submitted',
        detail: 'Every waiting submission is already under review.',
      };
}

function timestamp(iso: string | null): number {
  if (iso === null) return Number.POSITIVE_INFINITY;
  const parsed = Date.parse(iso);
  return Number.isNaN(parsed) ? Number.POSITIVE_INFINITY : parsed;
}

function plural(count: number, unit: string): string {
  return `${count} ${unit}${count === 1 ? '' : 's'}`;
}
