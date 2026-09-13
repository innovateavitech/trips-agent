import { describe, expect, it } from 'vitest';
import {
  changeTone,
  daysCovered,
  formatChange,
  formatDay,
  formatRatio,
  formatSize,
  lagosDay,
  spansYears,
  willBeQueued,
  windowOf,
} from '../analytics-rules';

describe('the Lagos day', () => {
  it('counts a booking taken just after midnight in Lagos on that Lagos day', () => {
    // 23:30 UTC on 9 March is 00:30 on 10 March in Lagos. The agent sold it on
    // the 10th, and the server files it under the 10th, so the screen has to
    // ask for the 10th or the two will never agree.
    expect(lagosDay(new Date('2026-03-09T23:30:00Z'))).toBe('2026-03-10');
  });

  it('is the same day for an instant in the middle of it', () => {
    expect(lagosDay(new Date('2026-03-10T12:00:00Z'))).toBe('2026-03-10');
  });

  it('builds an inclusive window ending today', () => {
    const window = windowOf(7, new Date('2026-03-10T12:00:00Z'));

    expect(window).toEqual({ from: '2026-03-04', to: '2026-03-10' });
    expect(daysCovered(window)).toBe(7);
  });
});

describe('how long a window is', () => {
  it('counts one day as one day', () => {
    expect(daysCovered({ from: '2026-03-10', to: '2026-03-10' })).toBe(1);
  });

  it('counts a backwards window as nothing', () => {
    expect(daysCovered({ from: '2026-03-10', to: '2026-03-01' })).toBe(0);
  });

  it('knows when a window crosses a year', () => {
    expect(spansYears({ from: '2025-12-20', to: '2026-01-10' })).toBe(true);
    expect(spansYears({ from: '2026-01-01', to: '2026-12-31' })).toBe(false);
  });
});

describe('what a report will do before the button is pressed', () => {
  const agencyReport = { alwaysAsynchronous: false, synchronousDayLimit: 90 };
  const platformReport = { alwaysAsynchronous: true, synchronousDayLimit: 90 };

  it('downloads a short agency report straight away', () => {
    expect(willBeQueued(agencyReport, { from: '2026-01-01', to: '2026-03-01' })).toBe(false);
  });

  it('keeps exactly ninety days on the fast path', () => {
    // The server's rule is "more than 90", so 90 is inside it. Pinned here too,
    // because the two saying different things is how a screen starts lying.
    expect(willBeQueued(agencyReport, { from: '2026-01-01', to: '2026-03-31' })).toBe(false);
    expect(daysCovered({ from: '2026-01-01', to: '2026-03-31' })).toBe(90);
  });

  it('queues one day longer than that', () => {
    expect(willBeQueued(agencyReport, { from: '2026-01-01', to: '2026-04-01' })).toBe(true);
  });

  it('queues a cross-agency report however short its window', () => {
    expect(willBeQueued(platformReport, { from: '2026-03-10', to: '2026-03-10' })).toBe(true);
  });
});

describe('change against the previous window', () => {
  it('reads basis points as a signed percentage', () => {
    expect(formatChange(1_250)).toBe('+12.5%');
    expect(formatChange(-500)).toBe('-5.0%');
    expect(formatChange(0)).toBe('0.0%');
  });

  it('has nothing to say when the previous window sold nothing', () => {
    // Not "+0%", which reads as flat, and not a huge number either.
    expect(formatChange(null)).toBeNull();
    expect(changeTone(null)).toBe('flat');
  });

  it('tells a rise from a fall', () => {
    expect(changeTone(1)).toBe('up');
    expect(changeTone(-1)).toBe('down');
    expect(changeTone(0)).toBe('flat');
  });
});

describe('ratios and sizes', () => {
  it('says there is nothing to divide by rather than showing zero', () => {
    expect(formatRatio(0, 0)).toBe('—');
    expect(formatRatio(1, 4)).toBe('25.0%');
  });

  it('reads a file size', () => {
    expect(formatSize(null)).toBe('—');
    expect(formatSize(512)).toBe('512 B');
    expect(formatSize(2_048)).toBe('2.0 KB');
  });
});

describe('showing a day', () => {
  it('reads a calendar day as that day, whatever the browser is set to', () => {
    // Parsed as UTC on purpose: '2026-03-09' is a date, not an instant, and a
    // browser west of Greenwich would otherwise draw it as the 8th.
    expect(formatDay('2026-03-09')).toBe('9 Mar');
    expect(formatDay('2026-03-09', true)).toBe('9 Mar 2026');
  });
});
