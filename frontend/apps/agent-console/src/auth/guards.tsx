import { Navigate, Outlet, useLocation, useSearchParams } from 'react-router-dom';
import { LoadingState } from '@trips/ui';
import { useAuth } from './auth-provider';
import { safeNextPath, signInPathFor } from './redirect';

/**
 * The authenticated route group's gate.
 *
 * Signed out → off to `/sign-in?next=<where they were going>`, so signing in
 * lands them on the page they asked for, query string and all. `replace`, so
 * Back does not return them to a page that would only bounce them again.
 */
export function RequireAuth() {
  const { session } = useAuth();
  const location = useLocation();

  if (session.status === 'restoring') {
    return <LoadingState size="page" label="Opening your console" className="min-h-screen" />;
  }

  if (session.status === 'signed-out') {
    const destination = `${location.pathname}${location.search}${location.hash}`;
    return <Navigate to={signInPathFor(destination, session.reason ?? undefined)} replace />;
  }

  return <Outlet />;
}

/**
 * The opposite gate, for sign-in and registration: someone already signed in
 * has no business there, and is sent on to wherever `next` says.
 *
 * This is also what completes a sign-in. The sign-in form only signs in; the
 * session changing to `signed-in` is what moves the agent on. One code path for
 * "signed in, now leave" rather than two that can disagree.
 */
export function RedirectIfSignedIn() {
  const { session } = useAuth();
  const [params] = useSearchParams();

  if (session.status === 'restoring') {
    return <LoadingState size="page" label="Checking your session" className="min-h-screen" />;
  }

  if (session.status === 'signed-in') {
    return <Navigate to={safeNextPath(params.get('next'))} replace />;
  }

  return <Outlet />;
}
