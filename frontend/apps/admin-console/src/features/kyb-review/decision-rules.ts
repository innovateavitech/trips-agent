import { ApiError, NetworkError, fieldError } from '../../lib/api/problem';
import type { KybSubmissionStatus } from './types';

/**
 * The rules around approving and rejecting, kept out of the component so they can be tested and
 * so the reason a button is disabled is a value, not a condition buried in JSX.
 *
 * None of this is the real check. The API enforces the permission, the state machine and the
 * mandatory reason on every call (#20). This exists so the reviewer hears about a problem before
 * they press the button, not after.
 */

/**
 * The shortest reason accepted. The API only refuses a blank one, but "no" or "bad docs" is a
 * support ticket in waiting: the agency has to be told what to fix.
 */
export const REJECTION_REASON_MIN = 10;

/**
 * The longest reason accepted — the width of kyb_submissions.rejection_reason (varchar 2000).
 * The API does not check this itself yet, so a longer reason would fail as a 500; the #66
 * breakdown suggests a 400 there too.
 */
export const REJECTION_REASON_MAX = 2000;

export type ReasonCheck = { ok: true; reason: string } | { ok: false; error: string };

export function validateRejectionReason(input: string): ReasonCheck {
  const reason = input.trim();

  if (reason === '') {
    return { ok: false, error: 'Write the reason. The agency cannot fix what it is not told.' };
  }

  if (reason.length < REJECTION_REASON_MIN) {
    return {
      ok: false,
      error: `Say a little more, at least ${REJECTION_REASON_MIN} characters, so the agency knows what to fix.`,
    };
  }

  if (reason.length > REJECTION_REASON_MAX) {
    return {
      ok: false,
      error: `Keep it to ${REJECTION_REASON_MAX} characters. This one is ${reason.length}.`,
    };
  }

  return { ok: true, reason };
}

/** Mirrors KybSubmission.IsAwaitingDecision in the domain. */
export function isAwaitingDecision(status: KybSubmissionStatus | string): boolean {
  return status === 'Submitted' || status === 'UnderReview';
}

export type DecisionFailureKind =
  | 'already-decided'
  | 'reason-invalid'
  | 'forbidden'
  | 'session-ended'
  | 'unknown-outcome'
  | 'failed';

export interface DecisionFailure {
  kind: DecisionFailureKind;
  title: string;
  detail: string;
}

/**
 * What to tell the reviewer when a decision did not come back as a success.
 *
 * The important distinction is between "it did not happen" and "we do not know". A 409 is a
 * clean no. A dropped connection or a 500 is an unknown: the server may have committed the
 * decision and failed afterwards. Saying "failed, try again" there invites a second decision on
 * top of the first — so the copy sends the reviewer to refresh and look first. It is the same
 * reasoning as ADR 0003 applies to ticket issuance.
 */
export function describeDecisionFailure(error: unknown): DecisionFailure {
  if (error instanceof NetworkError) {
    return {
      kind: 'unknown-outcome',
      title: 'We do not know whether your decision was saved',
      detail:
        'The connection dropped before the server answered. Refresh the submission to see where it stands before you decide again.',
    };
  }

  if (error instanceof ApiError) {
    switch (error.status) {
      case 409:
        return {
          kind: 'already-decided',
          title: 'Someone else decided this submission first',
          detail: 'Your decision was not saved. The page now shows the decision that was made.',
        };
      case 400:
        return {
          kind: 'reason-invalid',
          title:
            fieldError(error, 'reason') ?? error.problem?.title ?? 'The reason was not accepted.',
          detail: 'Nothing was sent to the agency.',
        };
      case 401:
        return {
          kind: 'session-ended',
          title: 'Your session has ended',
          detail: 'Nothing was changed. Sign in again, then reopen this submission.',
        };
      case 403:
        return {
          kind: 'forbidden',
          title: 'Your account cannot make KYB decisions',
          detail: 'It is missing the kyb.review permission. Nothing was changed.',
        };
      case 404:
        return {
          kind: 'failed',
          title: 'This submission no longer exists',
          detail: 'Go back to the queue and pick another.',
        };
      default:
        if (error.status >= 500) {
          return {
            kind: 'unknown-outcome',
            title: 'The server ran into a problem',
            detail:
              'Your decision may or may not have been saved. Refresh the submission to see where it stands before you decide again.',
          };
        }
    }
  }

  return {
    kind: 'failed',
    title: 'Your decision was not saved',
    detail: 'Try again in a moment.',
  };
}
