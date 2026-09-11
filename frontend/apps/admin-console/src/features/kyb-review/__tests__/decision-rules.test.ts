import { describe, expect, it } from 'vitest';
import { ApiError, NetworkError } from '../../../lib/api/problem';
import {
  REJECTION_REASON_MAX,
  REJECTION_REASON_MIN,
  describeDecisionFailure,
  isAwaitingDecision,
  validateRejectionReason,
} from '../decision-rules';

describe('validateRejectionReason', () => {
  it('accepts a real explanation and trims it', () => {
    expect(validateRejectionReason('  The certificate of incorporation is unreadable.  ')).toEqual({
      ok: true,
      reason: 'The certificate of incorporation is unreadable.',
    });
  });

  it.each(['', '   ', '\n\t '])('refuses a blank reason (%o)', (input) => {
    const result = validateRejectionReason(input);

    expect(result.ok).toBe(false);
    expect(result.ok === false && result.error).toContain('Write the reason');
  });

  it('refuses something too short to act on', () => {
    // "no" is a rejection the agency cannot do anything with.
    const result = validateRejectionReason('no');

    expect(result.ok).toBe(false);
    expect(result.ok === false && result.error).toContain(String(REJECTION_REASON_MIN));
  });

  it('accepts exactly the minimum', () => {
    expect(validateRejectionReason('a'.repeat(REJECTION_REASON_MIN)).ok).toBe(true);
  });

  it('accepts exactly the maximum', () => {
    expect(validateRejectionReason('a'.repeat(REJECTION_REASON_MAX)).ok).toBe(true);
  });

  it('refuses a reason longer than the column can hold', () => {
    // rejection_reason is varchar(2000); the API would fail on this, not validate it.
    const result = validateRejectionReason('a'.repeat(REJECTION_REASON_MAX + 1));

    expect(result.ok).toBe(false);
    expect(result.ok === false && result.error).toContain(String(REJECTION_REASON_MAX));
  });

  it('measures the trimmed reason, not the whitespace around it', () => {
    const padded = `  ${'a'.repeat(REJECTION_REASON_MAX)}  `;

    expect(validateRejectionReason(padded).ok).toBe(true);
  });
});

describe('isAwaitingDecision', () => {
  it.each([
    ['Submitted', true],
    ['UnderReview', true],
    ['Draft', false],
    ['Approved', false],
    ['Rejected', false],
  ])('%s → %s', (status, expected) => {
    expect(isAwaitingDecision(status)).toBe(expected);
  });
});

describe('describeDecisionFailure', () => {
  it('says plainly that a conflict changed nothing', () => {
    const failure = describeDecisionFailure(new ApiError(409, { title: 'Already decided' }));

    expect(failure.kind).toBe('already-decided');
    expect(failure.detail).toContain('not saved');
  });

  it('treats a lost connection as an UNKNOWN outcome, never as a failure', () => {
    // The decision may have been committed before the connection dropped. Telling the reviewer
    // "it failed, try again" invites a second decision on top of the first.
    const failure = describeDecisionFailure(new NetworkError(new TypeError('Failed to fetch')));

    expect(failure.kind).toBe('unknown-outcome');
    expect(failure.detail).toContain('Refresh the submission');
  });

  it('treats a server error the same way', () => {
    expect(describeDecisionFailure(new ApiError(500, null)).kind).toBe('unknown-outcome');
  });

  it('surfaces the server’s own message about the reason field', () => {
    const error = new ApiError(400, {
      title: 'One or more validation errors occurred.',
      errors: { reason: ['Say what the agency needs to fix. They see this text.'] },
    });

    expect(describeDecisionFailure(error)).toMatchObject({
      kind: 'reason-invalid',
      title: 'Say what the agency needs to fix. They see this text.',
    });
  });

  it('reads a Pascal-cased field name too', () => {
    const error = new ApiError(400, { errors: { Reason: ['Required.'] } });

    expect(describeDecisionFailure(error).title).toBe('Required.');
  });

  it.each([
    [401, 'session-ended'],
    [403, 'forbidden'],
    [404, 'failed'],
  ])('maps %i to %s', (status, kind) => {
    expect(describeDecisionFailure(new ApiError(status, null)).kind).toBe(kind);
  });
});
