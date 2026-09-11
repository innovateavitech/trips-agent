import { Clock, RefreshCw } from 'lucide-react';
import { Alert, Button, Card, ErrorState, Skeleton } from '@trips/ui';
import { ApiError, describeError } from '../../../api/errors';
import { cx } from '../class-names';
import { formatCountdown } from '../search-rules';

/**
 * The states around a result list: waiting, failed, and "these fares are about
 * to stop being true". Each is written for an agent with a customer on the
 * phone, because that is who is reading it.
 */

/**
 * Shaped like the results, because supplier search takes seconds and a blank
 * screen for that long reads as broken — and says how long it may take, so the
 * agent can tell their customer.
 */
export function ResultsSkeleton({ label }: { label: string }) {
  return (
    <div role="status" aria-live="polite" className="flex flex-col gap-4">
      <p className="flex items-center gap-2 text-sm text-muted-foreground">
        <span
          aria-hidden="true"
          className="h-4 w-4 animate-spin rounded-full border-2 border-primary border-t-transparent motion-reduce:animate-none"
        />
        {label} — this can take up to 20 seconds.
      </p>
      <div className="grid gap-6 lg:grid-cols-12">
        <div className="hidden lg:col-span-3 lg:block">
          <Skeleton className="h-96 w-full" />
        </div>
        <ul className="flex flex-col gap-3 lg:col-span-9" aria-hidden="true">
          {[0, 1, 2, 3].map((row) => (
            <li key={row}>
              <Card className="flex items-center gap-5 p-5">
                <Skeleton className="h-9 w-9 shrink-0 rounded-full" />
                <div className="flex flex-1 flex-col gap-2">
                  <Skeleton className="h-4 w-1/3" />
                  <Skeleton className="h-3 w-1/2" />
                </div>
                <div className="flex flex-col items-end gap-2">
                  <Skeleton className="h-7 w-28" />
                  <Skeleton className="h-9 w-20" />
                </div>
              </Card>
            </li>
          ))}
        </ul>
      </div>
    </div>
  );
}

/**
 * A failed search. Never an empty list — "no flights" and "we could not ask the
 * airline" are different answers, and confusing them loses a sale (FRD §2.3 RS-2).
 * Every version says nothing was booked or charged, because that is the first
 * thing an agent with money in play wants to know.
 */
export function SearchFailure({
  error,
  supplier,
  onRetry,
  retrying,
}: {
  error: unknown;
  /** Who we were asking: "airline" or "bus operator". */
  supplier: string;
  onRetry: () => void;
  retrying: boolean;
}) {
  const timedOut = error instanceof ApiError && (error.status === 504 || error.status === 503);
  const problem = describeError(error);

  return (
    <ErrorState
      title={timedOut ? `The ${supplier} systems did not answer in time` : problem.title}
      detail={
        <>
          {timedOut
            ? 'This happens when they are busy, and a second search usually works. '
            : `${problem.detail} `}
          Nothing has been booked or charged.
        </>
      }
      onRetry={onRetry}
      retrying={retrying}
    />
  );
}

/**
 * How long the fares on screen are held. Quiet while there is time; a warning
 * in the last two minutes; and once they have expired, an alert with the one
 * action that helps. Nothing expired can be selected — the cards see to that.
 */
export function FareExpiry({
  secondsLeft,
  onResearch,
}: {
  secondsLeft: number;
  onResearch: () => void;
}) {
  if (secondsLeft === 0) {
    return (
      <Alert
        tone="warning"
        title="These fares have expired"
        action={
          <Button size="sm" onClick={onResearch}>
            <RefreshCw className="h-4 w-4" aria-hidden="true" />
            Search again
          </Button>
        }
      >
        Suppliers hold a price for a few minutes only. Search again to see what they will sell at
        now — nothing has been booked.
      </Alert>
    );
  }

  const urgent = secondsLeft <= 120;

  return (
    // role="timer" is announced only when asked, so a screen reader is not read a number every second.
    <p
      role="timer"
      className={cx(
        'flex items-center gap-2 text-sm',
        urgent ? 'font-medium text-warning-subtle-foreground' : 'text-muted-foreground',
      )}
    >
      <Clock className="h-4 w-4" aria-hidden="true" />
      {/* One span, so the flex gap does not open a second space before the time. */}
      <span>
        Fares held for <span className="tabular-nums">{formatCountdown(secondsLeft)}</span>
        {urgent ? ' — choose soon, or search again for live prices' : null}
      </span>
    </p>
  );
}
