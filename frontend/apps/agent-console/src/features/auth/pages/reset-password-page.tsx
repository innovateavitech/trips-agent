import { useState, type FormEvent } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { Alert, Button, buttonVariants, PasswordInput } from '@trips/ui';
import { describeError, fieldError } from '../../../api/errors';
import {
  hasErrors,
  PASSWORD_HINT,
  validateNewPassword,
  type Errors,
  type NewPasswordValues,
} from '../account-rules';
import { resetPassword } from '../account-requests';

/**
 * Choose a new password, from the link in the reset email (#49).
 *
 * The token comes from the query string — `PasswordResetLinkBuilder` builds
 * `/reset-password?token=…`. It is never shown, never put in a form field and
 * never logged: it is a credential for the next 30 minutes.
 *
 * A successful reset revokes every refresh token the account holds, so anybody
 * already signed in elsewhere is signed out. That is the point of resetting, and
 * the screen says so rather than letting it be a surprise.
 */
export function ResetPasswordPage() {
  const [params] = useSearchParams();
  const token = params.get('token');

  const [values, setValues] = useState<NewPasswordValues>({ password: '', confirmation: '' });
  const [fieldErrors, setFieldErrors] = useState<Errors<NewPasswordValues>>({});
  const [failure, setFailure] = useState<unknown>(null);
  const [submitting, setSubmitting] = useState(false);
  const [done, setDone] = useState(false);

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!token) return;

    const errors = validateNewPassword(values);
    setFieldErrors(errors);
    setFailure(null);
    if (hasErrors(errors)) return;

    setSubmitting(true);
    try {
      await resetPassword(token, values.password);
      setDone(true);
    } catch (caught) {
      setFailure(caught);
    } finally {
      setSubmitting(false);
    }
  }

  // Somebody who typed the address by hand, or whose email client mangled the
  // link. Nothing to submit, so do not show a form that cannot work.
  if (!token) {
    return (
      <div className="flex flex-col gap-6">
        <div className="flex flex-col gap-1.5">
          <h1 className="text-2xl font-semibold tracking-tight text-foreground">
            This link is incomplete
          </h1>
          <p className="text-sm text-muted-foreground">
            Reset links carry a one-time code, and this one arrived without it. Ask for a fresh link
            and open it straight from your email.
          </p>
        </div>

        <Link to="/forgot-password" className={buttonVariants({ fullWidth: true })}>
          Email me a new link
        </Link>
      </div>
    );
  }

  if (done) {
    return (
      <div className="flex flex-col gap-6">
        <div className="flex flex-col gap-1.5">
          <h1 className="text-2xl font-semibold tracking-tight text-foreground">
            Your password is changed
          </h1>
          <p className="text-sm text-muted-foreground">
            Every device that was signed in has been signed out. Sign in again with your new
            password.
          </p>
        </div>

        <Link to="/sign-in" className={buttonVariants({ fullWidth: true })}>
          Sign in
        </Link>
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-6">
      <div className="flex flex-col gap-1.5">
        <h1 className="text-2xl font-semibold tracking-tight text-foreground">
          Choose a new password
        </h1>
        <p className="text-sm text-muted-foreground">
          This signs out every other device, including your phone.
        </p>
      </div>

      {failure ? <ResetFailure error={failure} /> : null}

      <form className="flex flex-col gap-4" onSubmit={handleSubmit} noValidate>
        <PasswordInput
          label="New password"
          autoComplete="new-password"
          hint={PASSWORD_HINT}
          value={values.password}
          onChange={(e) => setValues((current) => ({ ...current, password: e.target.value }))}
          error={fieldErrors.password ?? fieldError(failure, 'newPassword')}
          autoFocus
        />
        <PasswordInput
          label="Type it again"
          autoComplete="new-password"
          value={values.confirmation}
          onChange={(e) => setValues((current) => ({ ...current, confirmation: e.target.value }))}
          error={fieldErrors.confirmation}
        />
        <Button type="submit" fullWidth loading={submitting}>
          Change my password
        </Button>
      </form>

      <p className="text-center text-sm text-muted-foreground">
        <Link to="/sign-in" className="font-medium text-primary underline-offset-4 hover:underline">
          Back to sign in
        </Link>
      </p>
    </div>
  );
}

function ResetFailure({ error }: { error: unknown }) {
  const { title, detail } = describeError(error);

  // A used, expired or unknown token is a 400. The way out is a new link, so say
  // that instead of leaving somebody retrying a token that will never work.
  return (
    <Alert
      tone="destructive"
      title={title}
      action={
        <Link to="/forgot-password" className="font-medium underline underline-offset-4">
          Ask for a new link
        </Link>
      }
    >
      {detail}
    </Alert>
  );
}
