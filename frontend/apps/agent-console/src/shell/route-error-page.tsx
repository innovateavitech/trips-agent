import { Compass } from 'lucide-react';
import { Link, isRouteErrorResponse, useRouteError } from 'react-router-dom';
import { Button, EmptyState, ErrorState, buttonVariants } from '@trips/ui';

/** For a path no route serves. Rendered inside the shell, so navigation stays. */
export function NotFoundPage() {
  return (
    <EmptyState
      size="page"
      icon={<Compass aria-hidden="true" className="h-5 w-5" />}
      title="We could not find that page"
      action={
        <Link to="/" className={buttonVariants({ variant: 'outline', size: 'sm' })}>
          Go to the dashboard
        </Link>
      }
    >
      The link may be old, or the page may have moved. Everything in the console is reachable from
      the sidebar.
    </EmptyState>
  );
}

/**
 * The router's `errorElement`: a screen threw while rendering. Without it,
 * React Router shows its own developer-facing error page — fine on a laptop,
 * alarming in front of an agent.
 */
export function RouteErrorPage() {
  const error = useRouteError();

  if (isRouteErrorResponse(error) && error.status === 404) {
    return (
      <div className="px-4 py-10">
        <NotFoundPage />
      </div>
    );
  }

  return (
    <div className="mx-auto flex min-h-screen max-w-lg flex-col justify-center gap-4 px-4">
      <ErrorState
        title="This page stopped working"
        detail="Nothing you entered has been sent twice or lost on our side. Reload the page to carry on."
      />
      <div className="flex gap-2">
        <Button onClick={() => window.location.reload()}>Reload the page</Button>
        <a href="/" className={buttonVariants({ variant: 'outline' })}>
          Go to the dashboard
        </a>
      </div>
    </div>
  );
}
