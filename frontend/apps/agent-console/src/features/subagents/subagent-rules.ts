import type { Allowance, SubAgent, SubAgentStatus } from './types';

/**
 * ============================================================================
 *  What the sub-agent screens say, and when — as plain functions.
 * ============================================================================
 *
 * Kept out of the components so each rule can be tested without rendering
 * anything, and so the same wording cannot drift between the list and the
 * detail page.
 */

/** The badge next to a sub-agent's name. */
export function statusLabel(status: SubAgentStatus): string {
  switch (status) {
    case 'PendingVerification':
      return 'Awaiting verification';
    case 'Verified':
      return 'Active';
    case 'Rejected':
      return 'Verification refused';
    case 'Suspended':
      return 'Frozen';
    case 'Terminated':
      return 'Ended';
    default:
      return status;
  }
}

/** Which badge tone suits a status. Tokens only — no colour is named here. */
export function statusTone(
  status: SubAgentStatus,
): 'neutral' | 'success' | 'warning' | 'destructive' {
  switch (status) {
    case 'Verified':
      return 'success';
    case 'Suspended':
      return 'destructive';
    case 'Rejected':
      return 'destructive';
    case 'PendingVerification':
      return 'warning';
    default:
      return 'neutral';
  }
}

/** True when the sub-agent can still be frozen, unfrozen or revoked. */
export function canChangeStanding(subAgent: SubAgent): boolean {
  return subAgent.status !== 'Terminated';
}

/**
 * The one line that explains why a sub-agent cannot sell yet, or null when it
 * can. Ordered by what the agent should fix first.
 */
export function whyItCannotSell(subAgent: SubAgent): string | null {
  if (subAgent.status === 'Terminated') {
    return 'This sub-agent has been ended. Nothing here can be changed.';
  }

  if (subAgent.status === 'Suspended') {
    return 'Frozen. It can sign in and read, but cannot book until you lift the freeze.';
  }

  if (subAgent.hasOpenInvitation) {
    return 'Nobody has accepted the invitation yet, so nobody can sign in.';
  }

  if (subAgent.status === 'PendingVerification') {
    return 'Still being verified. It can prepare, but cannot sell yet.';
  }

  if (subAgent.scopeCount === 0) {
    return 'It has no scope, so it can sell nothing. Choose what it may sell.';
  }

  if (subAgent.allowanceLimitMinor === null) {
    return 'It has no spending allowance, so every booking is refused. Set one.';
  }

  if (subAgent.allowanceLimitMinor === 0) {
    return 'Its spending allowance is zero, so every booking is refused.';
  }

  return null;
}

/** How much of the allowance is used, 0 to 100. Zero-capped allowances read as full. */
export function allowanceUsedPercent(spentMinor: number, limitMinor: number): number {
  if (limitMinor <= 0) return 100;

  return Math.min(100, Math.round((spentMinor / limitMinor) * 100));
}

/** When an allowance is close enough to its cap to be worth saying so. */
export function isAllowanceNearlySpent(allowance: Allowance): boolean {
  return (
    allowance.status === 'Active' &&
    allowanceUsedPercent(allowance.spentMinor, allowance.limitMinor) >= 80
  );
}

/** The default range the network screen opens on: the last thirty days. */
export function defaultRange(now: Date = new Date()): { from: string; to: string } {
  const to = new Date(now);
  const from = new Date(now);
  from.setDate(from.getDate() - 30);

  return { from: from.toISOString(), to: to.toISOString() };
}

/** `2026-09-12` from an instant, for a date input. */
export function toDateInput(iso: string): string {
  return iso.slice(0, 10);
}

/** An instant from a date input, at the start of that day in UTC. */
export function fromDateInput(value: string, endOfDay = false): string {
  return `${value}T${endOfDay ? '23:59:59' : '00:00:00'}.000Z`;
}
