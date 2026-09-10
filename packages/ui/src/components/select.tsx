import type { SelectHTMLAttributes } from 'react';
import { forwardRef, useId } from 'react';
import { cn } from '../lib/cn';

export interface SelectProps extends SelectHTMLAttributes<HTMLSelectElement> {
  label: string;
  error?: string;
  hint?: string;
  /** Hides the label visually but keeps it for screen readers. */
  labelHidden?: boolean;
}

/**
 * A native `<select>`, styled to match `Input`.
 *
 * Deliberately native rather than a custom listbox: it is keyboard- and
 * screen-reader-correct with no work, and on a phone it opens the OS picker,
 * which is far easier to use one-handed than anything we would build.
 *
 * Like `Input`, the label is required — see input.tsx for why.
 */
export const Select = forwardRef<HTMLSelectElement, SelectProps>(function Select(
  { className, label, labelHidden, error, hint, id, children, ...props },
  ref,
) {
  const generatedId = useId();
  const selectId = id ?? generatedId;
  const describedBy = error ? `${selectId}-error` : hint ? `${selectId}-hint` : undefined;

  return (
    <div className="flex flex-col gap-1.5">
      <label
        htmlFor={selectId}
        className={cn('text-sm font-medium text-foreground', labelHidden && 'sr-only')}
      >
        {label}
      </label>

      <select
        ref={ref}
        id={selectId}
        aria-invalid={error ? true : undefined}
        aria-describedby={describedBy}
        className={cn(
          'h-10 w-full rounded-md border bg-background px-3 py-2 text-sm',
          'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2',
          'disabled:cursor-not-allowed disabled:opacity-50',
          error ? 'border-destructive' : 'border-input',
          className,
        )}
        {...props}
      >
        {children}
      </select>

      {hint && !error ? (
        <p id={`${selectId}-hint`} className="text-xs text-muted-foreground">
          {hint}
        </p>
      ) : null}

      {error ? (
        <p id={`${selectId}-error`} className="text-xs text-destructive" role="alert">
          {error}
        </p>
      ) : null}
    </div>
  );
});
