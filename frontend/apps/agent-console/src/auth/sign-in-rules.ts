/**
 * What the sign-in form checks before sending anything.
 *
 * Only what can be known without the server: that both fields are there and
 * the email is shaped like one. Whether the password is RIGHT is the API's
 * call, and it answers with one deliberately vague message either way.
 */

export interface SignInValues {
  email: string;
  password: string;
}

export type SignInErrors = Partial<Record<keyof SignInValues, string>>;

// Something@something.something, no spaces. Not an RFC 5322 parser — a typo
// catcher. The server is the real judge.
const EMAIL_SHAPE = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

export function validateSignIn(values: SignInValues): SignInErrors {
  const errors: SignInErrors = {};
  const email = values.email.trim();

  if (email.length === 0) {
    errors.email = 'Enter your email address.';
  } else if (!EMAIL_SHAPE.test(email)) {
    errors.email = 'Enter an email address like name@agency.com.';
  }

  // Not trimmed: a password can legitimately start or end with a space.
  if (values.password.length === 0) {
    errors.password = 'Enter your password.';
  }

  return errors;
}

export function hasErrors(errors: SignInErrors): boolean {
  return Object.keys(errors).length > 0;
}
