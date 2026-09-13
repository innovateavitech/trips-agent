/**
 * The one rule for "say why", shared by every screen that demands one.
 *
 * Suspending an agency, editing its profile, creating a back-office account, changing somebody's
 * role — all of them write a reason into `platform.audit_logs`, and all of them are refused by the
 * API below `AgencyLifecycleService.MinReasonLength`. One definition here, rather than a copy per
 * feature, is what stops the console and the server disagreeing about what counts as an answer.
 *
 * This is courtesy, not safety: the server checks it again on every request.
 */

/** Matches AgencyLifecycleService.MinReasonLength in the API. */
export const MIN_REASON_LENGTH = 10;

/** The longest, matching the audit log's reason column. */
export const MAX_REASON_LENGTH = 1000;

/** The message to show, or nothing when the reason will be accepted. */
export function validateReason(reason: string): string | undefined {
  const trimmed = reason.trim();

  if (trimmed.length === 0) {
    return 'Say why. It is recorded against your name in the audit log.';
  }

  if (trimmed.length < MIN_REASON_LENGTH) {
    return `Give at least ${MIN_REASON_LENGTH} characters — enough that it still makes sense in a year.`;
  }

  if (trimmed.length > MAX_REASON_LENGTH) {
    return `Keep it under ${MAX_REASON_LENGTH} characters.`;
  }

  return undefined;
}
