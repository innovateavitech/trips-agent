import { publicApi } from '../../api/public-client';
import { unwrap } from '../../api/errors';

/**
 * The four public account endpoints (#14–#17).
 *
 * All on `publicApi`, never the authenticated client: nobody registering or
 * resetting a password has a token, and a 401 from one of these must not set the
 * refresh machinery going.
 *
 * Three of them answer 202 rather than 200, and deliberately say the same thing
 * whether or not the address exists — telling the browser "no such account"
 * would turn this form into a way to find out who banks with us. The screens
 * repeat that vagueness rather than improving on it.
 */

export interface RegistrationRequest {
  businessName: string;
  firstName: string;
  lastName: string;
  email: string;
  phoneNumber: string;
  countryCode: string;
  password: string;
}

export async function register(values: RegistrationRequest): Promise<string> {
  const accepted = await unwrap(
    publicApi.POST('/api/v1/auth/register', {
      body: {
        businessName: values.businessName.trim(),
        firstName: values.firstName.trim(),
        lastName: values.lastName.trim(),
        email: values.email.trim(),
        // Blank means "not given". The column is nullable, and an empty string
        // would look like a phone number nobody can call.
        phoneNumber: values.phoneNumber.trim() || null,
        countryCode: values.countryCode,
        password: values.password,
      },
    }),
  );
  return accepted.message;
}

export async function verifyEmail(email: string, code: string): Promise<string> {
  const verified = await unwrap(
    publicApi.POST('/api/v1/auth/verify-email', {
      body: { email: email.trim(), code: code.trim() },
    }),
  );
  return verified.message;
}

export async function resendVerification(email: string): Promise<string> {
  const accepted = await unwrap(
    publicApi.POST('/api/v1/auth/resend-verification', { body: { email: email.trim() } }),
  );
  return accepted.message;
}

export async function requestPasswordReset(email: string): Promise<string> {
  const accepted = await unwrap(
    publicApi.POST('/api/v1/auth/forgot-password', { body: { email: email.trim() } }),
  );
  return accepted.message;
}

export async function resetPassword(token: string, newPassword: string): Promise<string> {
  const reset = await unwrap(
    publicApi.POST('/api/v1/auth/reset-password', { body: { token, newPassword } }),
  );
  return reset.message;
}
