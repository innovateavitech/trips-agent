/**
 * What the registration and password forms check before sending anything.
 *
 * Every rule here mirrors one the server enforces — `PasswordPolicy` and
 * `RegisterAgentHandler.Validate` in the backend. That duplication is deliberate:
 * checking here means somebody on a slow connection finds out about a six-letter
 * password immediately instead of after a round trip, and checking there means a
 * client that skips this — or lies — still cannot create a weak account.
 *
 * If the two ever disagree the server wins, and its message is what the form
 * shows: see `fieldError` in `api/errors.ts`.
 */

/** Backend: `PasswordPolicy.MinimumLength`. */
export const PASSWORD_MINIMUM_LENGTH = 8;

/** Backend: `PasswordPolicy.MaximumLength` — a guard against expensive hashing. */
export const PASSWORD_MAXIMUM_LENGTH = 128;

/** Backend: `SupportedMarkets.ByCountry`. Nigeria only, for now. */
export const SUPPORTED_COUNTRIES = [{ code: 'NG', name: 'Nigeria' }] as const;

/** How many digits are in an email verification code. Backend: `Tokens.cs`. */
export const VERIFICATION_CODE_LENGTH = 6;

// Something@something.something, no spaces. A typo catcher, not an RFC 5322
// parser — the same shape sign-in uses.
const EMAIL_SHAPE = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

const DIGITS_ONLY = /^\d+$/;
const CONTAINS_DIGIT = /\d/;

export type Errors<T> = Partial<Record<keyof T, string>>;

export function hasErrors(errors: object): boolean {
  return Object.keys(errors).length > 0;
}

/**
 * The password rules, in the order somebody hits them.
 *
 * Returns one message at a time rather than all of them: the server lists every
 * problem at once because it may never get another chance, but a form can point
 * at the field and let the person fix one thing.
 */
export function describePasswordProblem(password: string): string | undefined {
  if (password.length === 0) return 'Choose a password.';
  if (password.length < PASSWORD_MINIMUM_LENGTH) {
    return `Use at least ${PASSWORD_MINIMUM_LENGTH} characters.`;
  }
  if (password.length > PASSWORD_MAXIMUM_LENGTH) {
    return `Use at most ${PASSWORD_MAXIMUM_LENGTH} characters.`;
  }
  if (!CONTAINS_DIGIT.test(password)) return 'Include at least one number.';
  return undefined;
}

/** What the person sees under the field before they have typed anything wrong. */
export const PASSWORD_HINT = `At least ${PASSWORD_MINIMUM_LENGTH} characters, including a number.`;

export function describeEmailProblem(email: string): string | undefined {
  const trimmed = email.trim();
  if (trimmed.length === 0) return 'Enter your email address.';
  if (!EMAIL_SHAPE.test(trimmed)) return 'Enter an email address like name@agency.com.';
  return undefined;
}

export interface RegistrationValues {
  businessName: string;
  firstName: string;
  lastName: string;
  email: string;
  phoneNumber: string;
  countryCode: string;
  password: string;
}

export function validateRegistration(values: RegistrationValues): Errors<RegistrationValues> {
  const errors: Errors<RegistrationValues> = {};

  if (values.businessName.trim().length === 0) {
    errors.businessName = 'Enter your registered business name.';
  }
  if (values.firstName.trim().length === 0) errors.firstName = 'Enter your first name.';
  if (values.lastName.trim().length === 0) errors.lastName = 'Enter your last name.';

  const email = describeEmailProblem(values.email);
  if (email) errors.email = email;

  if (values.countryCode.trim().length === 0) {
    errors.countryCode = 'Choose the country your business is registered in.';
  } else if (!SUPPORTED_COUNTRIES.some((country) => country.code === values.countryCode)) {
    errors.countryCode = 'We cannot take registrations from that country yet.';
  }

  const password = describePasswordProblem(values.password);
  if (password) errors.password = password;

  // The phone number is optional, and the server accepts null. Nothing is
  // checked beyond emptiness on purpose: Nigerian numbers are written every way
  // imaginable — 0803…, +234803…, 234803… — and rejecting a real number because
  // it is punctuated unexpectedly is worse than storing it as given.

  return errors;
}

export interface VerificationValues {
  email: string;
  code: string;
}

export function validateVerification(values: VerificationValues): Errors<VerificationValues> {
  const errors: Errors<VerificationValues> = {};

  const email = describeEmailProblem(values.email);
  if (email) errors.email = email;

  const code = values.code.trim();
  if (code.length === 0) {
    errors.code = 'Enter the code from your email.';
  } else if (!DIGITS_ONLY.test(code) || code.length !== VERIFICATION_CODE_LENGTH) {
    errors.code = `The code is ${VERIFICATION_CODE_LENGTH} digits.`;
  }

  return errors;
}

export interface NewPasswordValues {
  password: string;
  confirmation: string;
}

export function validateNewPassword(values: NewPasswordValues): Errors<NewPasswordValues> {
  const errors: Errors<NewPasswordValues> = {};

  const password = describePasswordProblem(values.password);
  if (password) errors.password = password;

  if (values.confirmation.length === 0) {
    errors.confirmation = 'Type the password again.';
  } else if (values.confirmation !== values.password) {
    errors.confirmation = 'The two passwords do not match.';
  }

  return errors;
}

/**
 * Keeps only what a verification code can contain, so pasting "  123 456 "
 * from an email works. Trimmed to length as well: an extra digit is a typo, and
 * silently sending seven digits gets a puzzling rejection from the server.
 */
export function cleanVerificationCode(value: string): string {
  return value.replace(/\D/g, '').slice(0, VERIFICATION_CODE_LENGTH);
}
