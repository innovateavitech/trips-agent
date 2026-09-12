import { useState, type FormEvent } from 'react';
import { Navigate, useLocation } from 'react-router-dom';
import { Alert, Button, Card, CardContent, Input } from '@trips/ui';
import { BrandMark } from '../../components/brand-mark';
import { FullPageLoading } from '../../components/states';
import type { ErrorCopy } from '../../lib/api/problem';
import { safeRedirect } from '../../lib/auth/redirect';
import { useDocumentTitle } from '../../lib/hooks';
import { useAuth } from './auth-context';
import { describeSignInError } from './sign-in-errors';

interface SignInState {
  from?: unknown;
  signedOut?: boolean;
}

export function SignInPage() {
  useDocumentTitle('Sign in');

  const { status, session, signIn } = useAuth();
  const location = useLocation();
  const state = (location.state ?? null) as SignInState | null;

  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [fieldErrors, setFieldErrors] = useState<{ email?: string; password?: string }>({});
  const [failure, setFailure] = useState<ErrorCopy | null>(null);
  const [submitting, setSubmitting] = useState(false);

  if (status === 'restoring') return <FullPageLoading label="Signing you back in" />;

  // Signed in — either already, or just now. Go where they were heading, or to whichever screen
  // this account's role is actually for.
  if (status === 'signed-in')
    return <Navigate to={safeRedirect(state?.from, session?.claims)} replace />;

  async function submit(event: FormEvent) {
    event.preventDefault();

    const errors = {
      email: email.trim() === '' ? 'Enter your work email address.' : undefined,
      password: password === '' ? 'Enter your password.' : undefined,
    };
    setFieldErrors(errors);
    if (errors.email || errors.password) return;

    setFailure(null);
    setSubmitting(true);
    try {
      await signIn(email.trim(), password);
    } catch (error) {
      setFailure(describeSignInError(error));
      setPassword('');
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <main className="flex min-h-screen flex-col items-center justify-center gap-8 bg-muted px-4 py-12">
      <BrandMark />

      <Card className="w-full max-w-sm shadow-sm">
        <CardContent className="flex flex-col gap-5 p-6">
          <div className="flex flex-col gap-1">
            <h1 className="text-xl font-semibold tracking-tight text-foreground">Sign in</h1>
            <p className="text-sm text-muted-foreground">
              For Trips staff. Travel agencies sign in to the agent console.
            </p>
          </div>

          {state?.signedOut && !failure ? (
            <Alert tone="info">You have signed out. Sign in again to carry on reviewing.</Alert>
          ) : null}

          {failure ? (
            <Alert tone="destructive" title={failure.title}>
              {failure.detail}
            </Alert>
          ) : null}

          <form onSubmit={submit} noValidate className="flex flex-col gap-4">
            <Input
              label="Work email"
              type="email"
              autoComplete="username"
              autoFocus
              value={email}
              onChange={(event) => setEmail(event.target.value)}
              error={fieldErrors.email}
            />
            <Input
              label="Password"
              type="password"
              autoComplete="current-password"
              value={password}
              onChange={(event) => setPassword(event.target.value)}
              error={fieldErrors.password}
            />
            <Button type="submit" fullWidth loading={submitting}>
              Sign in
            </Button>
          </form>
        </CardContent>
      </Card>

      <p className="max-w-sm text-center text-xs text-muted-foreground">
        Every review decision is recorded against your name in the audit log.
      </p>
    </main>
  );
}
