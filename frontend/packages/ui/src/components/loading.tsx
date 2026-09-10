import { cva, type VariantProps } from 'class-variance-authority';
import { cn } from '../lib/cn';
import { Spinner } from './spinner';

const loadingVariants = cva('flex flex-col items-center justify-center gap-3 text-center', {
  variants: {
    size: {
      /** Inside a card or a panel. */
      inline: 'py-8',
      /** A whole route body. Tall enough that the shell does not collapse. */
      page: 'py-24',
    },
  },
  defaultVariants: { size: 'inline' },
});

export interface LoadingProps extends VariantProps<typeof loadingVariants> {
  /** Shown under the spinner. Keep it short — "Loading bookings", not a sentence. */
  message?: string;
  className?: string;
}

/**
 * The one loading state every screen uses.
 *
 * Built here, once, deliberately: the alternative is each feature screen
 * inventing its own spinner-and-message layout, and six months later nobody can
 * change how loading looks without touching thirty files.
 */
export function Loading({ message = 'Loading…', size, className }: LoadingProps) {
  return (
    <div role="status" aria-live="polite" className={cn(loadingVariants({ size }), className)}>
      <Spinner size={size === 'page' ? 'lg' : 'md'} className="text-primary" />
      <p className="text-sm text-muted-foreground">{message}</p>
    </div>
  );
}
