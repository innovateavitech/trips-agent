import { cva, type VariantProps } from 'class-variance-authority';
import type { HTMLAttributes } from 'react';
import { cn } from '../lib/cn';

const spinnerVariants = cva('animate-spin rounded-full border-current border-t-transparent', {
  variants: {
    size: {
      sm: 'h-4 w-4 border-2',
      md: 'h-6 w-6 border-2',
      lg: 'h-8 w-8 border-4',
    },
  },
  defaultVariants: { size: 'md' },
});

export interface SpinnerProps
  extends Omit<HTMLAttributes<HTMLSpanElement>, 'children'>, VariantProps<typeof spinnerVariants> {}

/**
 * The spinning glyph, and nothing else.
 *
 * Deliberately DECORATIVE — `aria-hidden`, no `role="status"`. Announcing the
 * wait is the caller's job: `<Loading>` wraps this in a live region with a
 * message. If the spinner announced itself too, a screen reader inside
 * `<Loading>` would meet two nested live regions and read the wait twice.
 *
 * It inherits colour from the parent's `text-*` token, so it works on a primary
 * button and on a plain background without needing a variant per surface.
 */
export function Spinner({ className, size, ...props }: SpinnerProps) {
  return (
    <span aria-hidden="true" className={cn('inline-flex', className)} {...props}>
      <span className={cn(spinnerVariants({ size }))} />
    </span>
  );
}
