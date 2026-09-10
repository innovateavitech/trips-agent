import { cva, type VariantProps } from 'class-variance-authority';
import type { HTMLAttributes, ReactNode } from 'react';
import { forwardRef } from 'react';
import { cn } from '../lib/cn';

const alertVariants = cva('rounded-lg border p-4 text-sm', {
  variants: {
    tone: {
      info: 'border-info-subtle bg-info-subtle text-info-subtle-foreground',
      success: 'border-success-subtle bg-success-subtle text-success-subtle-foreground',
      warning: 'border-warning-subtle bg-warning-subtle text-warning-subtle-foreground',
      destructive:
        'border-destructive-subtle bg-destructive-subtle text-destructive-subtle-foreground',
    },
  },
  defaultVariants: {
    tone: 'info',
  },
});

export interface AlertProps
  extends Omit<HTMLAttributes<HTMLDivElement>, 'title'>, VariantProps<typeof alertVariants> {
  title?: ReactNode;
  /** A button or link, e.g. "Complete verification". Rendered under the body. */
  action?: ReactNode;
}

/**
 * An inline message about the state of the page — a low wallet balance, a
 * blocked action, the outcome of a payment.
 *
 * `destructive` gets `role="alert"`, which screen readers interrupt for.
 * Everything else gets `role="status"`, which waits for a pause. A low-balance
 * warning is not worth cutting someone off mid-sentence.
 */
export const Alert = forwardRef<HTMLDivElement, AlertProps>(function Alert(
  { className, tone, title, action, children, ...props },
  ref,
) {
  return (
    <div
      ref={ref}
      role={tone === 'destructive' ? 'alert' : 'status'}
      className={cn(alertVariants({ tone }), className)}
      {...props}
    >
      {title ? <p className="font-medium">{title}</p> : null}
      {children ? <div className={cn(title && 'mt-1')}>{children}</div> : null}
      {action ? <div className="mt-3">{action}</div> : null}
    </div>
  );
});

export { alertVariants };
