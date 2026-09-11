import type { ReactNode } from 'react';
import { Alert, Button } from '@trips/ui';

/**
 * Loading, empty and error states for every admin screen.
 *
 * Built once here so later screens (the agency directory, the dashboard) pick these up rather
 * than each inventing its own "nothing here" message. Same shapes as the agent console's wallet
 * feature, so the two apps behave alike when things go wrong.
 */

export function EmptyState({
  title,
  children,
  action,
}: {
  title: string;
  children: ReactNode;
  action?: ReactNode;
}) {
  return (
    <div className="flex flex-col items-center gap-2 px-6 py-14 text-center">
      <p className="text-base font-medium text-foreground">{title}</p>
      <p className="max-w-md text-sm text-muted-foreground">{children}</p>
      {action ? <div className="mt-3">{action}</div> : null}
    </div>
  );
}

export function ErrorState({
  title,
  detail,
  onRetry,
  retrying,
}: {
  title: string;
  detail: string;
  onRetry?: () => void;
  retrying?: boolean;
}) {
  return (
    <Alert
      tone="destructive"
      title={title}
      action={
        onRetry ? (
          <Button size="sm" variant="outline" onClick={onRetry} loading={retrying}>
            Try again
          </Button>
        ) : null
      }
    >
      {detail}
    </Alert>
  );
}

/** For waits whose shape is unknown — restoring a session. Content-shaped waits use skeletons. */
export function FullPageLoading({ label }: { label: string }) {
  return (
    <div
      role="status"
      aria-live="polite"
      className="flex min-h-screen items-center justify-center gap-3 bg-background text-sm text-muted-foreground"
    >
      <span
        aria-hidden="true"
        className="h-4 w-4 animate-spin rounded-full border-2 border-current border-t-transparent"
      />
      {label}
    </div>
  );
}
