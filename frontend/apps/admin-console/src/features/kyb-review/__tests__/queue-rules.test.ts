import { describe, expect, it } from 'vitest';
import {
  REVIEW_TARGET_HOURS,
  applyQueueFilter,
  countByFilter,
  describeDuration,
  nextInQueue,
  parseQueueFilter,
  rankQueue,
  waitingTime,
} from '../queue-rules';
import type { KybQueueItem } from '../types';

const HOUR = 3_600_000;
const NOW = Date.parse('2026-09-11T09:00:00Z');

const item = (overrides: Partial<KybQueueItem> = {}): KybQueueItem => ({
  submissionId: 's1',
  agencyId: 'a1',
  agencyName: 'Lagos Travel',
  countryCode: 'NG',
  status: 'Submitted',
  submittedAt: '2026-09-11T08:00:00Z',
  documentCount: 2,
  ...overrides,
});

const hoursAgo = (hours: number) => new Date(NOW - hours * HOUR).toISOString();

describe('rankQueue', () => {
  it('puts the agency that has waited longest first', () => {
    const ranked = rankQueue([
      item({ submissionId: 'newest', submittedAt: hoursAgo(1) }),
      item({ submissionId: 'oldest', submittedAt: hoursAgo(50) }),
      item({ submissionId: 'middle', submittedAt: hoursAgo(10) }),
    ]);

    expect(ranked.map((entry) => entry.submissionId)).toEqual(['oldest', 'middle', 'newest']);
    expect(ranked.map((entry) => entry.position)).toEqual([1, 2, 3]);
  });

  it('does not change the array it was given', () => {
    const items = [
      item({ submissionId: 'b', submittedAt: hoursAgo(1) }),
      item({ submissionId: 'a', submittedAt: hoursAgo(9) }),
    ];

    rankQueue(items);

    expect(items.map((entry) => entry.submissionId)).toEqual(['b', 'a']);
  });

  it('sends submissions with no date to the back rather than to the front', () => {
    // Sorting nulls first would put them ahead of an agency that has waited three days.
    const ranked = rankQueue([
      item({ submissionId: 'undated', submittedAt: null }),
      item({ submissionId: 'dated', submittedAt: hoursAgo(2) }),
    ]);

    expect(ranked.map((entry) => entry.submissionId)).toEqual(['dated', 'undated']);
  });

  it('orders ties the same way every time', () => {
    const sameMoment = hoursAgo(3);
    const order = () =>
      rankQueue([
        item({ submissionId: 'b', submittedAt: sameMoment }),
        item({ submissionId: 'a', submittedAt: sameMoment }),
      ]).map((entry) => entry.submissionId);

    expect(order()).toEqual(order());
  });
});

describe('applyQueueFilter', () => {
  const ranked = rankQueue([
    item({ submissionId: 'one', status: 'Submitted', submittedAt: hoursAgo(30) }),
    item({ submissionId: 'two', status: 'UnderReview', submittedAt: hoursAgo(20) }),
    item({ submissionId: 'three', status: 'Submitted', submittedAt: hoursAgo(10) }),
  ]);

  it('shows everything by default', () => {
    expect(applyQueueFilter(ranked, 'all')).toHaveLength(3);
  });

  it('narrows to one status', () => {
    expect(applyQueueFilter(ranked, 'UnderReview').map((entry) => entry.submissionId)).toEqual([
      'two',
    ]);
  });

  it('keeps each submission’s place in the whole queue', () => {
    // Filtering is a view of the same line, so the second item is still number 2 — renumbering
    // would suggest the filtered item is next to be reviewed.
    expect(applyQueueFilter(ranked, 'Submitted').map((entry) => entry.position)).toEqual([1, 3]);
  });
});

describe('countByFilter', () => {
  it('counts each status and the whole queue', () => {
    const counts = countByFilter([
      item({ status: 'Submitted' }),
      item({ status: 'Submitted' }),
      item({ status: 'UnderReview' }),
    ]);

    expect(counts).toEqual({ all: 3, Submitted: 2, UnderReview: 1 });
  });
});

describe('waitingTime', () => {
  it('is calm inside the first day', () => {
    expect(waitingTime(hoursAgo(5), NOW)).toMatchObject({ tone: 'neutral', overdue: false });
  });

  it('warns after 24 hours', () => {
    expect(waitingTime(hoursAgo(25), NOW)).toMatchObject({ tone: 'warning', overdue: false });
  });

  it(`marks ${REVIEW_TARGET_HOURS} hours as over the target, not only past it`, () => {
    expect(waitingTime(hoursAgo(REVIEW_TARGET_HOURS), NOW)).toMatchObject({
      tone: 'destructive',
      overdue: true,
    });
  });

  it('handles a submission dated in the future without counting backwards', () => {
    // A server clock a few seconds ahead must not read as "waiting -1 hours".
    expect(waitingTime(new Date(NOW + HOUR).toISOString(), NOW)).toMatchObject({
      label: 'Under an hour',
      overdue: false,
    });
  });

  it.each([null, 'not a date'])('says so when the date is %o', (value) => {
    expect(waitingTime(value, NOW)).toMatchObject({ label: 'Unknown', tone: 'neutral' });
  });
});

describe('describeDuration', () => {
  it.each([
    [0, 'Under an hour'],
    [59 * 60_000, 'Under an hour'],
    [HOUR, '1 hour'],
    [5 * HOUR, '5 hours'],
    [24 * HOUR, '1 day'],
    [30 * HOUR, '1 day 6 hours'],
    [48 * HOUR, '2 days'],
    [9 * 24 * HOUR + 3 * HOUR, '9 days'],
  ])('reads %o ms as %o', (ms, expected) => {
    expect(describeDuration(ms)).toBe(expected);
  });
});

describe('nextInQueue', () => {
  it('offers the oldest submission that is not the one just decided', () => {
    const queue = [
      item({ submissionId: 'just-decided', submittedAt: hoursAgo(40) }),
      item({ submissionId: 'next', submittedAt: hoursAgo(20) }),
      item({ submissionId: 'later', submittedAt: hoursAgo(2) }),
    ];

    expect(nextInQueue(queue, 'just-decided')?.submissionId).toBe('next');
  });

  it('is null when nothing else is waiting', () => {
    expect(nextInQueue([item({ submissionId: 'only' })], 'only')).toBeNull();
    expect(nextInQueue([], 'anything')).toBeNull();
  });
});

describe('parseQueueFilter', () => {
  it.each([
    ['Submitted', 'Submitted'],
    ['UnderReview', 'UnderReview'],
    [null, 'all'],
    ['nonsense', 'all'],
    ['submitted', 'all'],
  ])('reads %o from the address bar as %o', (value, expected) => {
    expect(parseQueueFilter(value)).toBe(expected);
  });
});
