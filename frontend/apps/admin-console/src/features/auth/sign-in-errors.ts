import { ApiError, NetworkError, type ErrorCopy } from '../../lib/api/problem';

/** Thrown when an agency account signs in here. The session is revoked before this is thrown. */
export class NotStaffError extends Error {
  constructor() {
    super('This account does not belong to Trips staff.');
    this.name = 'NotStaffError';
  }
}

/**
 * What to tell someone whose sign-in failed.
 *
 * The API's own wording is used where it has one. It is deliberately vague about *why* — a wrong
 * password, an unknown address and a locked account all get the same 401 — so that the form
 * cannot be used to find out which staff email addresses exist. Nothing here undoes that.
 */
export function describeSignInError(error: unknown): ErrorCopy {
  if (error instanceof NotStaffError) {
    return {
      title: 'This console is for Trips staff',
      detail: 'That account belongs to a travel agency. Agencies sign in to the agent console.',
    };
  }

  if (error instanceof NetworkError) {
    return {
      title: 'Could not reach the server',
      detail: 'Check your connection, then try again.',
    };
  }

  if (error instanceof ApiError) {
    if (error.status === 401) {
      return {
        title: error.problem?.title ?? 'That email address and password do not match.',
        detail: 'Five wrong attempts lock an account for 15 minutes.',
      };
    }

    if (error.status === 403) {
      return {
        title: error.problem?.title ?? 'This account cannot sign in right now.',
        detail: error.problem?.detail ?? 'Ask a Super Admin to check your account.',
      };
    }

    if (error.status === 429) {
      return {
        title: 'Too many attempts',
        detail: 'Wait a few minutes before trying again.',
      };
    }
  }

  return {
    title: 'Sign-in failed',
    detail: 'The server ran into a problem. Try again in a moment.',
  };
}
