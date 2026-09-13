import type { AdminAlert, OperationsDashboard } from './types';

/** The badge tone for an alert's severity. No colour here — these are token names. */
export function severityTone(
  severity: AdminAlert['severity'],
): 'neutral' | 'warning' | 'destructive' {
  switch (severity) {
    case 'Critical':
      return 'destructive';
    case 'Warning':
      return 'warning';
    default:
      return 'neutral';
  }
}

/**
 * An alert type as a person would say it. `TicketTimeLimitBreach` → "Ticket time limit".
 *
 * Written as a mapping rather than by splitting the camel case, so the wording is ours: the enum
 * name is a programmer's label and some of them read badly when spaced out.
 */
const TYPE_LABELS: Record<string, string> = {
  PendingKyb: 'KYB waiting',
  GatewayError: 'Payment gateway',
  Dispute: 'Dispute',
  ReversalRequired: 'Reversal needed',
  TicketTimeLimitBreach: 'Ticket time limit',
  LedgerIntegrity: 'Books do not balance',
  SupplierBookingError: 'Supplier error',
};

export function alertTypeLabel(type: string): string {
  return TYPE_LABELS[type] ?? type;
}

/** Where an alert points, or null when it is about the platform rather than one agency. */
export function alertHref(alert: AdminAlert): string | null {
  return alert.agencyId ? `/agencies/${alert.agencyId}` : null;
}

/**
 * How old the numbers are, in words, and whether that is now a problem.
 *
 * Stale is not merely "past `staleAfter`" — the browser refetches, so a few seconds past is
 * ordinary. It becomes worth saying at the ten minutes the acceptance criterion names, which is
 * the point at which somebody should stop trusting what is on screen.
 */
export const STALENESS_LIMIT_MS = 10 * 60 * 1000;

export function freshness(
  dashboard: OperationsDashboard,
  now: number,
): { label: string; stale: boolean } {
  const age = now - new Date(dashboard.generatedAt).getTime();

  if (Number.isNaN(age) || age < 0) return { label: 'Counted just now', stale: false };

  const minutes = Math.floor(age / 60_000);
  const label =
    minutes < 1
      ? 'Counted less than a minute ago'
      : `Counted ${minutes} minute${minutes === 1 ? '' : 's'} ago`;

  return { label, stale: age > STALENESS_LIMIT_MS };
}

/** The one number that means somebody has paid and has nothing. Nothing outranks it. */
export function needsSomebodyNow(dashboard: OperationsDashboard): boolean {
  return dashboard.bookingsNeedingResolution > 0 || dashboard.criticalAlertCount > 0;
}
