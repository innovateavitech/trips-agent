import { useLocation } from 'react-router-dom';
import { EmptyState } from '@trips/ui';

interface RedirectState {
  from?: { pathname?: string; search?: string; hash?: string };
}

/**
 * PLACEHOLDER — the real sign-in form is issue #49.
 *
 * It exists so the guard in `require-auth.tsx` has somewhere to send an
 * anonymous visitor, and so the "preserve the intended destination" plumbing is
 * wired and testable before the form lands. #49 replaces the body of this file
 * and calls `useAuth().signIn(session)` on success, then navigates to
 * `intendedPath`.
 */
export function LoginPage() {
  const location = useLocation();
  const state = location.state as RedirectState | null;

  const from = state?.from;
  const intendedPath = from ? `${from.pathname ?? '/'}${from.search ?? ''}${from.hash ?? ''}` : '/';

  return (
    <main className="mx-auto flex min-h-screen max-w-md flex-col justify-center gap-6 p-6">
      <div className="flex flex-col gap-1 text-center">
        <h1 className="text-2xl font-semibold tracking-tight text-foreground">Trips Agent</h1>
        <p className="text-sm text-muted-foreground">Sign in to your agency console</p>
      </div>

      <EmptyState
        title="Sign-in form not built yet"
        description="Issue #49 builds this screen. The shell, routing and auth guard around it are already in place."
      />

      {from ? (
        <p className="text-center text-xs text-muted-foreground">
          You will be returned to{' '}
          <span className="font-medium text-foreground">{intendedPath}</span> after signing in.
        </p>
      ) : null}
    </main>
  );
}
