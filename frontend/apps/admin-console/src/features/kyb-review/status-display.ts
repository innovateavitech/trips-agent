import type { BadgeProps } from '@trips/ui';
import type { KybReviewDetail } from './types';

type Tone = NonNullable<BadgeProps['tone']>;

export interface StatusDisplay {
  label: string;
  tone: Tone;
}

/**
 * One table per status family, so the queue, the detail page and any later screen can never
 * disagree about what "UnderReview" is called or what colour it is.
 */
const SUBMISSION_STATUS: Record<string, StatusDisplay> = {
  Draft: { label: 'Draft', tone: 'neutral' },
  Submitted: { label: 'Submitted', tone: 'info' },
  UnderReview: { label: 'Under review', tone: 'primary' },
  Approved: { label: 'Approved', tone: 'success' },
  Rejected: { label: 'Rejected', tone: 'destructive' },
};

const AGENCY_STATUS: Record<string, StatusDisplay> = {
  PendingVerification: { label: 'Pending verification', tone: 'warning' },
  Verified: { label: 'Verified', tone: 'success' },
  Rejected: { label: 'Rejected', tone: 'destructive' },
  Suspended: { label: 'Suspended', tone: 'destructive' },
  Terminated: { label: 'Terminated', tone: 'neutral' },
};

export function submissionStatusDisplay(status: string): StatusDisplay {
  return SUBMISSION_STATUS[status] ?? { label: status, tone: 'neutral' };
}

export function agencyStatusDisplay(status: string): StatusDisplay {
  return AGENCY_STATUS[status] ?? { label: status, tone: 'neutral' };
}

/** The name the agency trades under, which is the one staff recognise; the legal name otherwise. */
export function agencyDisplayName(
  detail: Pick<KybReviewDetail, 'tradingName' | 'legalName'>,
): string {
  return detail.tradingName?.trim() || detail.legalName;
}
