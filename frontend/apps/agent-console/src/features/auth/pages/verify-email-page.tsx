import { useState, type FormEvent } from 'react';
import { Link, useLocation, useSearchParams } from 'react-router-dom';
import { Alert, Button, buttonVariants, Input } from '@trips/ui';
import { describeError, fieldError } from '../../../api/errors';
import {
  cleanVerificationCode,
  hasErrors,
  validateVerification,
  VERIFICATION_CODE_LENGTH,
  type Errors,
  type VerificationValues,
} from '../account-rules';
import { resendVerification, verifyEmail } from '../account-requests';

/**
 * Confirm the email address (#49).
 *
 * A six-digit code rather than a magic link, because a code can be read off a
 * phone and typed into the desktop the agency actually works on — and because
 * links in emails to Nigerian inboxes are rewritten by scanners often enough
 * that a link-only flow strands people.
 *
 * The address arrives in the query string when registration sends somebody here.
 * It is still editable: somebody who mistyped their email needs to be able to
 * correct it, and the resend below is how they get a code to the new address.
 */
export function VerifyEmailPage() {
  const [params] = useSearchParams();
  const location = useLocation();
  const justRegistered = (location.state as { justRegistered?: boolean } | null)?.justRegistered;

  const [values, setValues] = useState<VerificationValues>({
    email: params.get('email') ?? '',
    code: '',
  });
  const [fieldErrors, setFieldErrors] = useState<Errors<VerificationValues>>({});
  const [failure, setFailure] = useState<unknown>(null);
  const [submitting, setSubmitting] = useState(false);
  const [verified, setVerified] = useState(false);
  const [resent, setResent] = useState<string | null>(null);
  const [resending, setResending] = useState(false);

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();

    const errors = validateVerification(values);
    setFieldErrors(errors);
    setFailure(null);
    setResent(null);
    if (hasErrors(errors)) return;

    setSubmitting(true);
    try {
      await verifyEmail(values.email, values.code);
      setVerified(true);
    } catch (caught) {
      setFailure(caught);
    } finally {
      setSubmitting(false);
    }
  }

  async function handleResend() {
    const emailProblem = validateVerification({ ...values, code: '000000' }).email;
    if (emailProblem) {
      setFieldErrors({ email: emailProblem });
      return;
    }

    setResending(true);
    setFailure(null);
    try {
      setResent(await resendVerification(values.email));
    } catch (caught) {
      setFailure(caught);
    } finally {
      setResending(false);
    }
  }

  if (verified) {
    return (
      <div className="flex flex-col gap-6">
        <div className="flex flex-col gap-1.5">
          <h1 className="text-2xl font-semibold tracking-tight text-foreground">
            Your email is confirmed
          </h1>
          <p className="text-sm text-muted-foreground">
            Sign in to finish setting up — the next step is verifying your business, which is what
            lets you book and take payment.
          </p>
        </div>

        {/* Styled as a button but genuinely a link: it navigates, and a
            <button> inside an <a> is not valid HTML. */}
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
          Confirm your email
        </h1>
        <p className="text-sm text-muted-foreground">
          We sent a {VERIFICATION_CODE_LENGTH}-digit code
          {values.email ? (
            <>
              {' '}
              to <span className="font-medium text-foreground">{values.email}</span>
            </>
          ) : (
            ' to the address you registered with'
          )}
          . It expires in 24 hours.
        </p>
      </div>

      {justRegistered && !failure && !resent ? (
        <Alert tone="success" title="Account created">
          Check your inbox — and your spam folder, which is where a first email from a new sender
          often lands.
        </Alert>
      ) : null}

      {resent ? <Alert tone="info">{resent}</Alert> : null}

      {failure ? <VerificationFailure error={failure} /> : null}

      <form className="flex flex-col gap-4" onSubmit={handleSubmit} noValidate>
        <Input
          label="Email address"
          type="email"
          inputMode="email"
          autoComplete="email"
          value={values.email}
          onChange={(e) => setValues((current) => ({ ...current, email: e.target.value }))}
          error={fieldErrors.email ?? fieldError(failure, 'email')}
        />

        <Input
          label="Verification code"
          // `inputMode` rather than type="number": a code is a string of digits,
          // not a quantity, and a number input brings spinners and lets somebody
          // scroll the value.
          inputMode="numeric"
          autoComplete="one-time-code"
          maxLength={VERIFICATION_CODE_LENGTH}
          className="text-center text-lg tracking-widest"
          value={values.code}
          onChange={(e) =>
            setValues((current) => ({ ...current, code: cleanVerificationCode(e.target.value) }))
          }
          error={fieldErrors.code ?? fieldError(failure, 'code')}
          autoFocus
        />

        <Button type="submit" fullWidth loading={submitting}>
          Confirm my email
        </Button>
      </form>

      <div className="flex flex-col gap-2 text-center text-sm text-muted-foreground">
        <p>
          No code yet?{' '}
          <button
            type="button"
            onClick={handleResend}
            disabled={resending}
            className="font-medium text-primary underline-offset-4 hover:underline disabled:opacity-50"
          >
            {resending ? 'Sending…' : 'Send another'}
          </button>
        </p>
        <p>
          <Link
            to="/sign-in"
            className="font-medium text-primary underline-offset-4 hover:underline"
          >
            Back to sign in
          </Link>
        </p>
      </div>
    </div>
  );
}

function VerificationFailure({ error }: { error: unknown }) {
  const { title, detail } = describeError(error);

  // A wrong or expired code is a 400. Worth its own wording: the useful thing to
  // know is that asking for another one works, not that something went wrong.
  return (
    <Alert tone="destructive" title={title}>
      {detail}
    </Alert>
  );
}
