import type { ReactNode } from 'react';
import { cn } from '../lib/cn';

export interface EmptyStateProps {
  /** What is not here. "No bookings yet", not "Empty". */
  title: string;
  /**
   * Why it is empty and what to do about it. An empty screen with no next step
   * reads as a broken screen — an agent who has just signed up and sees a blank
   * bookings table cannot tell whether it failed to load.
   */
  description?: string;
  /** Usually a `<Button>` that starts the thing that would fill this screen. */
  action?: ReactNode;
  /** Optional decorative glyph. Kept out of the accessibility tree. */
  icon?: ReactNode;
  className?: string;
}

/**
 * Shown when a request succeeded and the answer was "nothing".
 *
 * Distinct from `<ErrorState>` on purpose. "You have no bookings" and "we could
 * not load your bookings" demand different actions from the agent, and merging
 * them into one grey box hides a real failure behind a normal-looking screen.
 */
export function EmptyState({ title, description, action, icon, className }: EmptyStateProps) {
  return (
    <div
      className={cn(
        'flex flex-col items-center justify-center gap-3 rounded-lg border border-dashed border-border px-6 py-12 text-center',
        className,
      )}
    >
      {icon ? (
        <div aria-hidden="true" className="text-muted-foreground">
          {icon}
        </div>
      ) : null}

      <div className="flex flex-col gap-1">
        <h3 className="text-base font-medium text-foreground">{title}</h3>
        {description ? (
          <p className="max-w-sm text-sm text-muted-foreground">{description}</p>
        ) : null}
      </div>

      {action ? <div className="mt-1">{action}</div> : null}
    </div>
  );
}
