import { describe, expect, it } from 'vitest';
import { navigationFor } from '../../../app/navigation';
import {
  allowanceUsedPercent,
  canChangeStanding,
  defaultRange,
  fromDateInput,
  isAllowanceNearlySpent,
  statusLabel,
  statusTone,
  toDateInput,
  whyItCannotSell,
} from '../subagent-rules';
import type { Allowance, SubAgent } from '../types';

function subAgent(patch: Partial<SubAgent> = {}): SubAgent {
  return {
    id: 'sub-1',
    legalName: 'Ikeja Branch Limited',
    tradingName: 'Ikeja Travel',
    slug: 'ikeja-branch',
    status: 'Verified',
    statusReason: null,
    createdAt: '2026-07-02T09:00:00Z',
    hasOpenInvitation: false,
    scopeCount: 2,
    deniedPermissionCount: 0,
    canSeeMargin: true,
    allowanceSpentMinor: 100_000,
    allowanceLimitMinor: 1_000_000,
    allowanceCurrency: 'NGN',
    ...patch,
  };
}

describe('why a sub-agent cannot sell', () => {
  it('says nothing when everything is in place', () => {
    expect(whyItCannotSell(subAgent())).toBeNull();
  });

  it('names the freeze first, because lifting it comes before anything else', () => {
    expect(whyItCannotSell(subAgent({ status: 'Suspended', scopeCount: 0 }))).toContain('Frozen');
  });

  it('says an ended sub-agent cannot be changed at all', () => {
    expect(whyItCannotSell(subAgent({ status: 'Terminated' }))).toContain('ended');
  });

  it('points at the unaccepted invitation before the verification', () => {
    const waiting = subAgent({ status: 'PendingVerification', hasOpenInvitation: true });

    expect(whyItCannotSell(waiting)).toContain('invitation');
  });

  it('says an empty scope means it can sell nothing', () => {
    // Fails closed: no scope is not "everything", it is "nothing".
    expect(whyItCannotSell(subAgent({ scopeCount: 0 }))).toContain('sell nothing');
  });

  it('treats a missing allowance and a zero allowance as different problems', () => {
    expect(whyItCannotSell(subAgent({ allowanceLimitMinor: null }))).toContain(
      'no spending allowance',
    );
    expect(whyItCannotSell(subAgent({ allowanceLimitMinor: 0 }))).toContain('zero');
  });
});

describe('allowance usage', () => {
  it('is a whole percentage of the cap', () => {
    expect(allowanceUsedPercent(640_000, 1_000_000)).toBe(64);
    expect(allowanceUsedPercent(0, 1_000_000)).toBe(0);
  });

  it('never goes past 100, even after the cap is lowered below what was spent', () => {
    expect(allowanceUsedPercent(1_500_000, 1_000_000)).toBe(100);
  });

  it('reads a zero cap as fully used rather than dividing by nothing', () => {
    expect(allowanceUsedPercent(0, 0)).toBe(100);
  });

  it('warns from four fifths, and not while frozen', () => {
    const allowance = (patch: Partial<Allowance>): Allowance => ({
      subAgencyId: 'sub-1',
      currency: 'NGN',
      spentMinor: 800_000,
      limitMinor: 1_000_000,
      remainingMinor: 200_000,
      period: 'Monthly',
      status: 'Active',
      resetsAt: '2026-10-01T00:00:00Z',
      ...patch,
    });

    expect(isAllowanceNearlySpent(allowance({}))).toBe(true);
    expect(isAllowanceNearlySpent(allowance({ spentMinor: 700_000 }))).toBe(false);

    // A frozen allowance is refused outright, so "nearly spent" is not the point.
    expect(isAllowanceNearlySpent(allowance({ status: 'Frozen' }))).toBe(false);
  });
});

describe('status wording', () => {
  it('calls a suspension a freeze, which is what the principal did', () => {
    expect(statusLabel('Suspended')).toBe('Frozen');
    expect(statusTone('Suspended')).toBe('destructive');
  });

  it('calls a termination an end, and refuses further changes', () => {
    expect(statusLabel('Terminated')).toBe('Ended');
    expect(canChangeStanding(subAgent({ status: 'Terminated' }))).toBe(false);
    expect(canChangeStanding(subAgent({ status: 'Suspended' }))).toBe(true);
  });
});

describe('the date range', () => {
  it('opens on the last thirty days', () => {
    const range = defaultRange(new Date('2026-09-12T10:00:00Z'));

    expect(toDateInput(range.from)).toBe('2026-08-13');
    expect(toDateInput(range.to)).toBe('2026-09-12');
  });

  it('sends instants in UTC, because the API refuses any other offset', () => {
    expect(fromDateInput('2026-09-12')).toBe('2026-09-12T00:00:00.000Z');
    expect(fromDateInput('2026-09-12', true)).toBe('2026-09-12T23:59:59.000Z');
  });
});

describe('the sidebar for each kind of agency', () => {
  it('offers a principal its network', () => {
    const labels = navigationFor('principal').flatMap((section) =>
      section.items.map((item) => item.label),
    );

    expect(labels).toContain('Sub-agents');
    expect(labels).toContain('Network performance');
  });

  it('does not offer a sub-agent a network of its own', () => {
    // Two levels only, so "Sub-agents" would be a dead end for one. Its own
    // figures are still worth showing, and the same endpoint serves both.
    const labels = navigationFor('sub_agent').flatMap((section) =>
      section.items.map((item) => item.label),
    );

    expect(labels).not.toContain('Sub-agents');
    expect(labels).toContain('Network performance');
  });

  it('leaves no empty section behind', () => {
    expect(navigationFor('sub_agent').every((section) => section.items.length > 0)).toBe(true);
  });
});
