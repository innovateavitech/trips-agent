import type { ReactNode } from 'react';
import { Alert, Button } from '@trips/ui';

/**
 * Empty and error states for the wallet screens.
 *
 * Issue #48 builds these as app-wide shared components. These two live here
 * until it lands, so that work has one obvious place to absorb them from
 * rather than a dozen bespoke messages scattered through the feature.
 */

export function EmptyState({ title, children }: { title: string; children: ReactNode }) {
  return (
    <div className="flex flex-col items-center gap-1 px-6 py-12 text-center">
      <p className="text-sm font-medium text-foreground">{title}</p>
      <p className="max-w-sm text-sm text-muted-foreground">{children}</p>
    </div>
  );
}

export function ErrorState({
  title,
  detail,
  onRetry,
}: {
  title: string;
  detail: string;
  onRetry: () => void;
}) {
  return (
    <Alert
      tone="destructive"
      title={title}
      action={
        <Button size="sm" variant="outline" onClick={onRetry}>
          Try again
        </Button>
      }
    >
      {detail}
    </Alert>
  );
}
