import { useState, type FormEvent } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { Alert, Button, Input, PasswordInput, Select } from '@trips/ui';
import { describeError, fieldError } from '../../../api/errors';
import {
  hasErrors,
  PASSWORD_HINT,
  SUPPORTED_COUNTRIES,
  validateRegistration,
  type Errors,
  type RegistrationValues,
} from '../account-rules';
import { register } from '../account-requests';

const EMPTY: RegistrationValues = {
  businessName: '',
  firstName: '',
  lastName: '',
  email: '',
  phoneNumber: '',
  countryCode: 'NG',
  password: '',
};

/**
 * Register an agency (#49, FRD §2.2 UC-1A).
 *
 * One account is created and it owns the agency — there is no "join an existing
 * agency" here, because an invited colleague arrives through a team invitation
 * instead. The agency starts unverified: registering does not let anybody sell
 * anything until KYB is approved, which is what /verification is for (#50).
 *
 * On success the server answers 202, not 201. It has accepted the details and
 * emailed a code; the account is not usable until that code comes back. So this
 * hands straight over to /verify-email rather than pretending to be finished.
 */
export function RegisterPage() {
  const navigate = useNavigate();

  const [values, setValues] = useState(EMPTY);
  const [fieldErrors, setFieldErrors] = useState<Errors<RegistrationValues>>({});
  const [failure, setFailure] = useState<unknown>(null);
  const [submitting, setSubmitting] = useState(false);

  function update<K extends keyof RegistrationValues>(field: K, value: RegistrationValues[K]) {
    setValues((current) => ({ ...current, [field]: value }));
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();

    const errors = validateRegistration(values);
    setFieldErrors(errors);
    setFailure(null);
    if (hasErrors(errors)) return;

    setSubmitting(true);
    try {
      await register(values);
      navigate(`/verify-email?email=${encodeURIComponent(values.email.trim())}`, {
        state: { justRegistered: true },
      });
    } catch (caught) {
      setFailure(caught);
      setSubmitting(false);
    }
  }

  // A 400 carries the server's own per-field messages. Shown in place of ours,
  // because the server knows things this form cannot — that the email is already
  // registered, for one.
  const serverError = (field: string) => fieldError(failure, field);

  return (
    <div className="flex flex-col gap-6">
      <div className="flex flex-col gap-1.5">
        <h1 className="text-2xl font-semibold tracking-tight text-foreground">
          Register your agency
        </h1>
        <p className="text-sm text-muted-foreground">
          Takes a minute. You can look around before your business is verified.
        </p>
      </div>

      {failure && !hasServerFieldErrors(failure) ? <RegistrationFailure error={failure} /> : null}

      <form className="flex flex-col gap-4" onSubmit={handleSubmit} noValidate>
        <Input
          label="Business name"
          autoComplete="organization"
          hint="As it appears on your CAC registration."
          value={values.businessName}
          onChange={(e) => update('businessName', e.target.value)}
          error={fieldErrors.businessName ?? serverError('businessName')}
          autoFocus
        />

        <div className="grid gap-4 sm:grid-cols-2">
          <Input
            label="First name"
            autoComplete="given-name"
            value={values.firstName}
            onChange={(e) => update('firstName', e.target.value)}
            error={fieldErrors.firstName ?? serverError('firstName')}
          />
          <Input
            label="Last name"
            autoComplete="family-name"
            value={values.lastName}
            onChange={(e) => update('lastName', e.target.value)}
            error={fieldErrors.lastName ?? serverError('lastName')}
          />
        </div>

        <Input
          label="Work email"
          type="email"
          inputMode="email"
          autoComplete="email"
          hint="We send a code here to confirm it."
          value={values.email}
          onChange={(e) => update('email', e.target.value)}
          error={fieldErrors.email ?? serverError('email')}
        />

        <Input
          label="Phone number"
          type="tel"
          inputMode="tel"
          autoComplete="tel"
          hint="Optional. How we reach you about a booking that needs a decision."
          value={values.phoneNumber}
          onChange={(e) => update('phoneNumber', e.target.value)}
          error={fieldErrors.phoneNumber ?? serverError('phoneNumber')}
        />

        <Select
          label="Country of registration"
          hint="Nigeria only for now — more markets are coming."
          value={values.countryCode}
          onChange={(e) => update('countryCode', e.target.value)}
          error={fieldErrors.countryCode ?? serverError('countryCode')}
        >
          {SUPPORTED_COUNTRIES.map((country) => (
            <option key={country.code} value={country.code}>
              {country.name}
            </option>
          ))}
        </Select>

        <PasswordInput
          label="Password"
          autoComplete="new-password"
          hint={PASSWORD_HINT}
          value={values.password}
          onChange={(e) => update('password', e.target.value)}
          error={fieldErrors.password ?? serverError('password')}
        />

        <Button type="submit" fullWidth loading={submitting}>
          Create my account
        </Button>
      </form>

      <p className="text-center text-sm text-muted-foreground">
        Already registered?{' '}
        <Link to="/sign-in" className="font-medium text-primary underline-offset-4 hover:underline">
          Sign in
        </Link>
      </p>
    </div>
  );
}

/**
 * True when the error already appears under the fields. Repeating it in a banner
 * as well reads as two separate problems.
 */
function hasServerFieldErrors(error: unknown): boolean {
  return [
    'businessName',
    'firstName',
    'lastName',
    'email',
    'phoneNumber',
    'countryCode',
    'password',
  ].some((field) => fieldError(error, field) !== undefined);
}

function RegistrationFailure({ error }: { error: unknown }) {
  const { title, detail } = describeError(error);
  return (
    <Alert tone="destructive" title={title}>
      {detail}
    </Alert>
  );
}
