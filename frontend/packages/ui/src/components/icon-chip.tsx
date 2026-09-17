import { cva, type VariantProps } from 'class-variance-authority';
import type { HTMLAttributes } from 'react';
import { cn } from '../lib/cn';

/**
 * A tinted square behind a row's leading icon — an upcoming trip, an alert in
 * "For you today". Tones reuse the same `*-subtle` pairs `Badge` uses, so an
 * icon chip and a status pill for the same kind of thing always agree.
 */
const iconChipVariants = cva(
  'inline-flex size-10 shrink-0 items-center justify-center rounded-[10px]',
  {
    variants: {
      tone: {
        neutral: 'bg-muted text-muted-foreground',
        info: 'bg-info-subtle text-info-subtle-foreground',
        success: 'bg-success-subtle text-success-subtle-foreground',
        warning: 'bg-warning-subtle text-warning-subtle-foreground',
        destructive: 'bg-destructive-subtle text-destructive-subtle-foreground',
      },
    },
    defaultVariants: { tone: 'neutral' },
  },
);

export interface IconChipProps
  extends HTMLAttributes<HTMLDivElement>, VariantProps<typeof iconChipVariants> {}

export function IconChip({ className, tone, ...props }: IconChipProps) {
  return <div className={cn(iconChipVariants({ tone }), className)} {...props} />;
}

export { iconChipVariants };
