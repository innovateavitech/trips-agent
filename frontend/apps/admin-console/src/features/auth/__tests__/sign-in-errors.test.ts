import { describe, expect, it } from 'vitest';
import { ApiError, NetworkError } from '../../../lib/api/problem';
import { NotStaffError, describeSignInError } from '../sign-in-errors';

describe('describeSignInError', () => {
  it('sends an agency account to the agent console', () => {
    expect(describeSignInError(new NotStaffError()).detail).toContain('agent console');
  });

  it('repeats the API’s deliberately vague answer to a bad sign-in', () => {
    // The API gives one answer for a wrong password, an unknown address and a locked account, so
    // the form cannot be used to discover which staff addresses exist. Do not improve on it.
    const error = new ApiError(401, { title: 'That email address and password do not match.' });

    expect(describeSignInError(error).title).toBe('That email address and password do not match.');
  });

  it('passes on why a verified account still cannot sign in', () => {
    const error = new ApiError(403, {
      title: 'Your email address has not been verified.',
      detail: 'Check your inbox for the code, or ask for a new one.',
    });

    expect(describeSignInError(error)).toEqual({
      title: 'Your email address has not been verified.',
      detail: 'Check your inbox for the code, or ask for a new one.',
    });
  });

  it('tells the reviewer the server is unreachable rather than blaming their password', () => {
    const copy = describeSignInError(new NetworkError(new TypeError('Failed to fetch')));

    expect(copy.title).toBe('Could not reach the server');
  });

  it('has something to say about an error it has never seen', () => {
    const copy = describeSignInError(new Error('boom'));

    expect(copy.title).toBe('Sign-in failed');
    expect(copy.detail.length).toBeGreaterThan(0);
  });
});
