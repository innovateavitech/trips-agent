import { describe, expect, it } from 'vitest';
import {
  cleanVerificationCode,
  describePasswordProblem,
  validateNewPassword,
  validateRegistration,
  validateVerification,
  type RegistrationValues,
} from '../account-rules';

const complete: RegistrationValues = {
  businessName: 'Lagos Travel',
  firstName: 'Adaeze',
  lastName: 'Okafor',
  email: 'owner@lagostravel.example.com',
  phoneNumber: '08031234567',
  countryCode: 'NG',
  password: 'correct horse 9',
};

describe('describePasswordProblem', () => {
  it('accepts a password that meets the policy', () => {
    expect(describePasswordProblem('passw0rd')).toBeUndefined();
  });

  it('mirrors the minimum length the server enforces', () => {
    expect(describePasswordProblem('pass1')).toBe('Use at least 8 characters.');
  });

  it('requires a number, as the server does', () => {
    expect(describePasswordProblem('passwordpassword')).toBe('Include at least one number.');
  });

  it('rejects one longer than the server will hash', () => {
    expect(describePasswordProblem(`${'a'.repeat(128)}1`)).toBe('Use at most 128 characters.');
  });

  it('asks for a password rather than complaining twice when the field is empty', () => {
    expect(describePasswordProblem('')).toBe('Choose a password.');
  });

  it('counts a long passphrase with one digit as fine, without demanding symbols', () => {
    expect(describePasswordProblem('the 7 sailing dinghies')).toBeUndefined();
  });
});

describe('validateRegistration', () => {
  it('passes a complete form', () => {
    expect(validateRegistration(complete)).toEqual({});
  });

  it('accepts a blank phone number, which the server stores as null', () => {
    expect(validateRegistration({ ...complete, phoneNumber: '' })).toEqual({});
  });

  it.each([
    ['08031234567', 'as dialled locally'],
    ['+234 803 123 4567', 'with a country code and spaces'],
    ['0803-123-4567', 'punctuated with dashes'],
  ])('accepts %s (%s)', (phoneNumber) => {
    expect(validateRegistration({ ...complete, phoneNumber })).toEqual({});
  });

  it('refuses a country the platform does not serve yet', () => {
    expect(validateRegistration({ ...complete, countryCode: 'GH' }).countryCode).toBe(
      'We cannot take registrations from that country yet.',
    );
  });

  it('treats a business name of only spaces as missing', () => {
    expect(validateRegistration({ ...complete, businessName: '   ' }).businessName).toBe(
      'Enter your registered business name.',
    );
  });

  it('reports every missing field at once, so the form is filled in one pass', () => {
    const errors = validateRegistration({
      businessName: '',
      firstName: '',
      lastName: '',
      email: '',
      phoneNumber: '',
      countryCode: '',
      password: '',
    });
    expect(Object.keys(errors).sort()).toEqual([
      'businessName',
      'countryCode',
      'email',
      'firstName',
      'lastName',
      'password',
    ]);
  });
});

describe('validateVerification', () => {
  it('accepts six digits', () => {
    expect(validateVerification({ email: complete.email, code: '123456' })).toEqual({});
  });

  it.each(['12345', '1234567', '12a456'])('rejects %s', (code) => {
    expect(validateVerification({ email: complete.email, code }).code).toBe('The code is 6 digits.');
  });

  it('asks for the code when it is blank rather than describing its shape', () => {
    expect(validateVerification({ email: complete.email, code: '' }).code).toBe(
      'Enter the code from your email.',
    );
  });
});

describe('validateNewPassword', () => {
  it('accepts two matching, acceptable passwords', () => {
    expect(validateNewPassword({ password: 'passw0rd', confirmation: 'passw0rd' })).toEqual({});
  });

  it('catches a mistyped confirmation', () => {
    expect(
      validateNewPassword({ password: 'passw0rd', confirmation: 'passw0rdd' }).confirmation,
    ).toBe('The two passwords do not match.');
  });

  it('does not call two equally weak passwords a mismatch', () => {
    const errors = validateNewPassword({ password: 'short', confirmation: 'short' });
    expect(errors.password).toBe('Use at least 8 characters.');
    expect(errors.confirmation).toBeUndefined();
  });
});

describe('cleanVerificationCode', () => {
  it('keeps the digits out of a pasted code', () => {
    expect(cleanVerificationCode('  123 456 ')).toBe('123456');
  });

  it('stops at six digits, so an extra keystroke is not sent to the server', () => {
    expect(cleanVerificationCode('1234567')).toBe('123456');
  });

  it('is empty when there is nothing to keep', () => {
    expect(cleanVerificationCode('code:')).toBe('');
  });
});
