import { cva, type VariantProps } from 'class-variance-authority';
import type { ReactNode } from 'react';
import { cn } from '../lib/cn';
import { Alert } from './alert';
import { Button } from './button';

/**
 * ============================================================================
 *  The three states every screen that loads data has to handle.
 * ============================================================================
 *
 * Built once, here, so that "loading", "nothing yet" and "that failed" look and
 * read the same on every screen. A console where each screen invents its own
 * spinner and its own "Oops!" feels assembled rather than designed — and the
 * inconsistent ones are always the ones nobody tested.
 *
 *   isPending → <LoadingState />   (or a <Skeleton /> when you know the shape)
 *   isError   → <ErrorState />
 *   empty     → <EmptyState />
 */

const stateVariants = cva('flex flex-col items-center justify-center text-center', {
  variants: {
    size: {
      /** Inside a card or a table body. */
      section: 'gap-2 px-6 py-12',
      /** The whole content area — a route that has nothing else to show yet. */
      page: 'min-h-80 gap-3 px-6 py-16',
    },
  },
  defaultVariants: {
    size: 'section',
  },
});

type StateSize = VariantProps<typeof stateVariants>['size'];

export interface LoadingStateProps {
  /** Read out by screen readers, and shown under the spinner on `page` size. */
  label?: string;
  size?: StateSize;
  className?: string;
}

/**
 * For waits whose shape you do not know. `role="status"` makes a screen reader
 * announce the label once, politely, instead of leaving the agent in silence.
 */
export function LoadingState({ label = 'Loading', size, className }: LoadingStateProps) {
  return (
    <div role="status" aria-live="polite" className={cn(stateVariants({ size }), className)}>
      <span
        aria-hidden="true"
        className="h-6 w-6 animate-spin rounded-full border-2 border-primary border-t-transparent motion-reduce:animate-none"
      />
      <span className={size === 'page' ? 'text-sm text-muted-foreground' : 'sr-only'}>{label}</span>
    </div>
  );
}

export interface EmptyStateProps {
  title: string;
  /** What will appear here, and how to make it appear. Never just "Nothing here". */
  children?: ReactNode;
  /** An icon, drawn at 20px. Optional — most empty states read fine without one. */
  icon?: ReactNode;
  /** The next step: usually one button or link that fills this space. */
  action?: ReactNode;
  size?: StateSize;
  className?: string;
}

/**
 * An empty list is an invitation to act, not a dead end. Say what will live
 * here and offer the one action that puts it there.
 */
export function EmptyState({ title, children, icon, action, size, className }: EmptyStateProps) {
  return (
    <div className={cn(stateVariants({ size }), className)}>
      {icon ? (
        <div className="mb-1 flex h-10 w-10 items-center justify-center rounded-full bg-muted text-muted-foreground">
          {icon}
        </div>
      ) : null}
      <p className="text-sm font-medium text-foreground">{title}</p>
      {children ? <div className="max-w-sm text-sm text-muted-foreground">{children}</div> : null}
      {action ? <div className="mt-2">{action}</div> : null}
    </div>
  );
}

export interface ErrorStateProps {
  /** What failed, from the agent's side: "We could not load your wallet". */
  title: string;
  /** What it means and what to do. Reassure where it matters — money, bookings. */
  detail?: ReactNode;
  /** Shows a "Try again" button. Leave it out when trying again cannot help. */
  onRetry?: () => void;
  /** Puts the retry button into its loading state while a refetch is running. */
  retrying?: boolean;
  className?: string;
}

/**
 * A failed load. Uses the destructive `Alert`, so screen readers are told
 * immediately (`role="alert"`), and offers a retry only when one is possible.
 */
export function ErrorState({ title, detail, onRetry, retrying, className }: ErrorStateProps) {
  return (
    <Alert
      tone="destructive"
      title={title}
      className={className}
      action={
        onRetry ? (
          <Button size="sm" variant="outline" onClick={onRetry} loading={retrying}>
            Try again
          </Button>
        ) : undefined
      }
    >
      {detail}
    </Alert>
  );
}
