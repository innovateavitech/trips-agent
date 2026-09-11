import { useState, type FormEvent } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { Alert, Button, Input } from '@trips/ui';
import { ApiError, describeError } from '../../api/errors';
import { AUTH_MODE } from '../../app/settings';
import { useAuth } from '../auth-provider';
import { readSignInReason } from '../redirect';
import { hasErrors, validateSignIn, type SignInErrors } from '../sign-in-rules';

/**
 * Sign in. Deliberately the only finished auth screen in #48 — the guard needs
 * somewhere to send people. Registration, email verification and password
 * reset are #49, and their routes already exist.
 *
 * It does not navigate on success. Signing in changes the session, and
 * `RedirectIfSignedIn` — the same gate that stops a signed-in agent seeing this
 * page — sends them on to `next`.
 */
export function SignInPage() {
  const { signIn } = useAuth();
  const [params] = useSearchParams();
  const reason = readSignInReason(params.get('reason'));

  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [fieldErrors, setFieldErrors] = useState<SignInErrors>({});
  const [failure, setFailure] = useState<unknown>(null);
  const [submitting, setSubmitting] = useState(false);

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const errors = validateSignIn({ email, password });
    setFieldErrors(errors);
    setFailure(null);
    if (hasErrors(errors)) return;

    setSubmitting(true);
    try {
      await signIn({ email: email.trim(), password });
    } catch (caught) {
      setFailure(caught);
      setSubmitting(false);
    }
  }

  return (
    <div className="flex flex-col gap-6">
      <div className="flex flex-col gap-1.5">
        <h1 className="text-2xl font-semibold tracking-tight text-foreground">
          Sign in to your console
        </h1>
        <p className="text-sm text-muted-foreground">
          Use the email address your agency registered with.
        </p>
      </div>

      {reason === 'expired' && !failure ? (
        <Alert tone="info" title="Your session ended">
          For your security, you were signed out after a period away. Sign in to pick up where you
          left off.
        </Alert>
      ) : null}

      {reason === 'unavailable' && !failure ? (
        <Alert tone="warning" title="We could not check your session">
          The server did not answer. Sign in again, or try in a moment.
        </Alert>
      ) : null}

      {AUTH_MODE === 'mock' ? (
        <Alert tone="info" title="Demo mode">
          No server is involved: any email signs you in. Use the password “wrong” to see a failed
          sign-in.
        </Alert>
      ) : null}

      {failure ? <SignInFailure error={failure} /> : null}

      <form className="flex flex-col gap-4" onSubmit={handleSubmit} noValidate>
        <Input
          label="Email address"
          type="email"
          autoComplete="email"
          inputMode="email"
          value={email}
          onChange={(e) => setEmail(e.target.value)}
          error={fieldErrors.email}
          autoFocus
        />
        <div className="flex flex-col gap-1.5">
          <Input
            label="Password"
            type="password"
            autoComplete="current-password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            error={fieldErrors.password}
          />
          <Link
            to="/forgot-password"
            className="self-end text-xs font-medium text-primary underline-offset-4 hover:underline"
          >
            Forgot your password?
          </Link>
        </div>
        <Button type="submit" fullWidth loading={submitting}>
          Sign in
        </Button>
      </form>

      <p className="text-center text-sm text-muted-foreground">
        New to Trips?{' '}
        <Link
          to="/register"
          className="font-medium text-primary underline-offset-4 hover:underline"
        >
          Register your agency
        </Link>
      </p>
    </div>
  );
}

function SignInFailure({ error }: { error: unknown }) {
  // 403 means the password was RIGHT but the account cannot sign in yet —
  // unverified email, or suspended. Worth a calmer tone than "wrong password".
  if (error instanceof ApiError && error.status === 403) {
    return (
      <Alert tone="warning" title={error.title}>
        {error.detail}
      </Alert>
    );
  }

  const { title, detail } = describeError(error);
  const unreachable = error instanceof TypeError;

  return (
    <Alert tone="destructive" title={title}>
      {error instanceof ApiError && error.status === 401 ? 'Check both and try again.' : detail}
      {unreachable && import.meta.env.DEV ? (
        <span className="mt-1 block">
          In development, start the API, or set VITE_AUTH_MODE=mock to try the console without it.
        </span>
      ) : null}
    </Alert>
  );
}
