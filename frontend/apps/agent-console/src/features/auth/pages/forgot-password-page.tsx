import { useState, type FormEvent } from 'react';
import { Link } from 'react-router-dom';
import { Alert, Button, Input } from '@trips/ui';
import { describeError } from '../../../api/errors';
import { describeEmailProblem } from '../account-rules';
import { requestPasswordReset } from '../account-requests';

/**
 * Ask for a password reset link (#49).
 *
 * The server answers 202 and the same message whether or not the address has an
 * account, and this screen does not improve on that. Saying "no account with
 * that email" here would let anybody check which travel agencies are on the
 * platform, and confirm a working address to phish.
 *
 * So on success the screen stops asking and reports what was sent, without
 * claiming the address exists.
 */
export function ForgotPasswordPage() {
  const [email, setEmail] = useState('');
  const [emailError, setEmailError] = useState<string | undefined>(undefined);
  const [failure, setFailure] = useState<unknown>(null);
  const [submitting, setSubmitting] = useState(false);
  const [sent, setSent] = useState(false);

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();

    const problem = describeEmailProblem(email);
    setEmailError(problem);
    setFailure(null);
    if (problem) return;

    setSubmitting(true);
    try {
      await requestPasswordReset(email);
      setSent(true);
    } catch (caught) {
      setFailure(caught);
    } finally {
      setSubmitting(false);
    }
  }

  if (sent) {
    return (
      <div className="flex flex-col gap-6">
        <div className="flex flex-col gap-1.5">
          <h1 className="text-2xl font-semibold tracking-tight text-foreground">Check your email</h1>
          <p className="text-sm text-muted-foreground">
            If <span className="font-medium text-foreground">{email.trim()}</span> has an account, a
            reset link is on its way. It works once, and expires in 30 minutes.
          </p>
        </div>

        <Alert tone="info" title="Nothing arrived?">
          Check your spam folder. If it is not there either, the address may not have an account —
          try another, or register a new agency.
        </Alert>

        <div className="flex flex-col gap-2 text-center text-sm text-muted-foreground">
          <p>
            <button
              type="button"
              onClick={() => setSent(false)}
              className="font-medium text-primary underline-offset-4 hover:underline"
            >
              Use a different address
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

  return (
    <div className="flex flex-col gap-6">
      <div className="flex flex-col gap-1.5">
        <h1 className="text-2xl font-semibold tracking-tight text-foreground">
          Reset your password
        </h1>
        <p className="text-sm text-muted-foreground">
          We will email you a link that works once, for 30 minutes.
        </p>
      </div>

      {failure ? <ResetRequestFailure error={failure} /> : null}

      <form className="flex flex-col gap-4" onSubmit={handleSubmit} noValidate>
        <Input
          label="Email address"
          type="email"
          inputMode="email"
          autoComplete="email"
          value={email}
          onChange={(e) => setEmail(e.target.value)}
          error={emailError}
          autoFocus
        />
        <Button type="submit" fullWidth loading={submitting}>
          Email me a reset link
        </Button>
      </form>

      <p className="text-center text-sm text-muted-foreground">
        Remembered it?{' '}
        <Link to="/sign-in" className="font-medium text-primary underline-offset-4 hover:underline">
          Sign in
        </Link>
      </p>
    </div>
  );
}

function ResetRequestFailure({ error }: { error: unknown }) {
  const { title, detail } = describeError(error);
  return (
    <Alert tone="destructive" title={title}>
      {detail}
    </Alert>
  );
}
