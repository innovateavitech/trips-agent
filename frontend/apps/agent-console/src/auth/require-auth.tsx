import { Navigate, Outlet, useLocation } from 'react-router-dom';
import { Loading } from '@trips/ui';
import { useAuth } from './auth-context';

/**
 * Wraps every authenticated route group.
 *
 * Three states, and the middle one matters: while the silent bootstrap is still
 * deciding whether the refresh cookie is valid we must render NEITHER the app
 * nor a redirect. Redirecting during bootstrap bounces an already-signed-in
 * agent to the login screen on every page refresh, which is the single most
 * common way this pattern is got wrong.
 */
export function RequireAuth() {
  const { status } = useAuth();
  const location = useLocation();

  if (status === 'bootstrapping') {
    return <Loading size="page" message="Signing you in…" />;
  }

  if (status === 'anonymous') {
    /**
     * `state.from` preserves where they were heading, including the query
     * string and hash — an agent who follows a link to a specific booking and
     * has to log in should land on that booking, not a generic dashboard.
     *
     * `replace` keeps the guarded URL out of history, so Back from the login
     * screen does not bounce through the redirect again.
     */
    return <Navigate to="/login" replace state={{ from: location }} />;
  }

  return <Outlet />;
}

/**
 * The mirror image: keeps a signed-in agent off the login and registration
 * screens. Without it, someone who bookmarks /login sees a login form while
 * already having a session.
 */
export function RequireAnonymous() {
  const { status } = useAuth();

  if (status === 'bootstrapping') {
    return <Loading size="page" />;
  }

  if (status === 'authenticated') {
    return <Navigate to="/" replace />;
  }

  return <Outlet />;
}
