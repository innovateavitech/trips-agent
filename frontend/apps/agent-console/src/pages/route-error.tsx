import { isRouteErrorResponse, useRouteError } from 'react-router-dom';
import { ErrorState } from '@trips/ui';

/**
 * Catches anything a route throws — a failed loader, or a render-time crash in
 * a feature screen — so one broken component shows a message instead of a white
 * page. Without this the agent sees nothing at all and assumes the product is
 * down.
 */
export function RouteErrorPage() {
  const error = useRouteError();

  let detail: string | undefined;
  if (isRouteErrorResponse(error)) {
    detail = `${error.status} ${error.statusText}`;
  } else if (error instanceof Error) {
    detail = error.message;
  }

  return (
    <div className="p-6">
      <ErrorState
        title="This screen could not load"
        description="Reload the page. If it keeps happening, send us the detail below."
        detail={detail}
        onRetry={() => window.location.reload()}
        retryLabel="Reload"
      />
    </div>
  );
}
