import { cva, type VariantProps } from 'class-variance-authority';
import type { HTMLAttributes } from 'react';
import { forwardRef } from 'react';
import { cn } from '../lib/cn';

/**
 * A small status pill: a transaction type, a booking state, a KYB status.
 *
 * Tones use the `*-subtle` token pairs, not the solid ones. Solid `--warning`
 * as text on white is roughly 3.4:1 — below WCAG AA — and a badge is small
 * text, which is exactly where that matters most.
 */
const badgeVariants = cva(
  [
    'inline-flex items-center gap-1 whitespace-nowrap',
    'rounded-full px-2 py-0.5 text-xs font-medium',
  ],
  {
    variants: {
      tone: {
        neutral: 'bg-muted text-muted-foreground',
        primary: 'bg-primary-subtle text-primary',
        success: 'bg-success-subtle text-success-subtle-foreground',
        warning: 'bg-warning-subtle text-warning-subtle-foreground',
        destructive: 'bg-destructive-subtle text-destructive-subtle-foreground',
        info: 'bg-info-subtle text-info-subtle-foreground',
      },
    },
    defaultVariants: {
      tone: 'neutral',
    },
  },
);

export interface BadgeProps
  extends HTMLAttributes<HTMLSpanElement>, VariantProps<typeof badgeVariants> {}

export const Badge = forwardRef<HTMLSpanElement, BadgeProps>(function Badge(
  { className, tone, ...props },
  ref,
) {
  return <span ref={ref} className={cn(badgeVariants({ tone }), className)} {...props} />;
});

export { badgeVariants };
