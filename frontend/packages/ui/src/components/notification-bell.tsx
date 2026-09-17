import { cn } from '../lib/cn';
import { NotificationIcon } from './icons';

export interface NotificationBellProps {
  count?: number;
  onClick?: () => void;
  className?: string;
}

/**
 * An icon button with an unread count. The count is a prop, not fetched here
 * — this component has no opinion on where the number comes from, mock or
 * real, only on how to show one once it exists.
 */
export function NotificationBell({ count = 0, onClick, className }: NotificationBellProps) {
  const label = count > 99 ? '99+' : count > 0 ? String(count) : null;

  return (
    <button
      type="button"
      onClick={onClick}
      aria-label={label ? `Notifications, ${label} unread` : 'Notifications'}
      className={cn(
        'relative inline-flex size-10 items-center justify-center rounded-full bg-card',
        'hover:bg-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring',
        className,
      )}
    >
      <NotificationIcon size={20} />
      {label ? (
        <span
          className="absolute -top-0.5 right-0 rounded-full bg-destructive px-1 py-px text-2xs font-semibold leading-tight text-destructive-foreground"
          aria-hidden="true"
        >
          {label}
        </span>
      ) : null}
    </button>
  );
}
