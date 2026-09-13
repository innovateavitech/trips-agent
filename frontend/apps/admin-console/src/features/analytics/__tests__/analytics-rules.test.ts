import { describe, expect, it } from 'vitest';
import {
  changeTone,
  errorTone,
  formatBasisPoints,
  formatDay,
  lagosDay,
  spansYears,
  windowOf,
} from '../analytics-rules';

describe('the day the aggregates are keyed by', () => {
  it('is the Lagos day, not the UTC one', () => {
    // 23:30 UTC on 9 March is 00:30 on 10 March in Lagos. The rollup files that
    // booking under the 10th, so the console has to ask for the 10th.
    expect(lagosDay(new Date('2026-03-09T23:30:00Z'))).toBe('2026-03-10');
    expect(lagosDay(new Date('2026-03-10T12:00:00Z'))).toBe('2026-03-10');
  });

  it('builds an inclusive window ending today', () => {
    expect(windowOf(7, new Date('2026-03-10T12:00:00Z'))).toEqual({
      from: '2026-03-04',
      to: '2026-03-10',
    });
  });

  it('knows when a window needs the year shown', () => {
    expect(spansYears({ from: '2025-12-20', to: '2026-01-10' })).toBe(true);
    expect(spansYears({ from: '2026-01-01', to: '2026-12-31' })).toBe(false);
  });

  it('reads a calendar day as that day whatever the browser is set to', () => {
    expect(formatDay('2026-03-09')).toBe('9 Mar');
    expect(formatDay('2026-03-09', true)).toBe('9 Mar 2026');
  });
});

describe('ratios sent as basis points', () => {
  it('reads them as a percentage', () => {
    expect(formatBasisPoints(250)).toBe('2.5%');
    expect(formatBasisPoints(10_000)).toBe('100.0%');
  });

  it('signs a change when asked', () => {
    expect(formatBasisPoints(1_250, true)).toBe('+12.5%');
    expect(formatBasisPoints(-1_250, true)).toBe('-12.5%');
  });

  it('says there is nothing to divide by rather than showing zero', () => {
    // Null is "nobody searched", which is not the same fact as "nobody booked".
    expect(formatBasisPoints(null)).toBe('—');
    expect(changeTone(null)).toBe('flat');
  });
});

describe('how worried to be about a supplier', () => {
  it('is calm when nothing failed', () => {
    expect(errorTone(0)).toBe('calm');
    expect(errorTone(499)).toBe('calm');
  });

  it('asks for attention past five per cent', () => {
    expect(errorTone(500)).toBe('attention');
    expect(errorTone(1_499)).toBe('attention');
  });

  it('is urgent past fifteen', () => {
    expect(errorTone(1_500)).toBe('urgent');
  });

  it('is calm when no calls were made at all', () => {
    // No calls is not a perfect record and not a bad one. It is no information,
    // and a red badge on it would send somebody chasing nothing.
    expect(errorTone(null)).toBe('calm');
  });
});
