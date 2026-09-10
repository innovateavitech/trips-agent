import { Button } from './button';
import { cn } from '../lib/cn';

export interface ErrorStateProps {
  /** Plain language, no status codes. "Could not load your bookings". */
  title?: string;
  /** What the agent can do next. Defaults to a generic retry prompt. */
  description?: string;
  /**
   * Shown in a muted line under the description — the API's `detail` or the
   * error message. Safe to pass through: it helps support, and an agent who
   * screenshots it gives us something to work with.
   */
  detail?: string;
  /** Wire this to TanStack Query's `refetch`. Omit it if a retry is meaningless. */
  onRetry?: () => void;
  retryLabel?: string;
  className?: string;
}

/**
 * Shown when a request failed.
 *
 * Never silently swallow a failure into an empty state — an agent staring at
 * "no bookings" when the API is down will assume their bookings are gone. This
 * is the difference between a quiet bug and a support call, and on a product
 * holding people's money the support call is the better outcome.
 */
export function ErrorState({
  title = 'Something went wrong',
  description = 'That did not load. Try again, and let us know if it keeps happening.',
  detail,
  onRetry,
  retryLabel = 'Try again',
  className,
}: ErrorStateProps) {
  return (
    <div
      role="alert"
      className={cn(
        'flex flex-col items-center justify-center gap-3 rounded-lg border border-destructive/30 bg-destructive/5 px-6 py-12 text-center',
        className,
      )}
    >
      <div className="flex flex-col gap-1">
        <h3 className="text-base font-medium text-foreground">{title}</h3>
        <p className="max-w-sm text-sm text-muted-foreground">{description}</p>
        {detail ? <p className="max-w-sm text-xs text-muted-foreground">{detail}</p> : null}
      </div>

      {onRetry ? (
        <Button variant="outline" size="sm" onClick={onRetry} className="mt-1">
          {retryLabel}
        </Button>
      ) : null}
    </div>
  );
}
