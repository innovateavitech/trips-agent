import type { InputHTMLAttributes, ReactNode } from 'react';
import { forwardRef, useId } from 'react';
import { cn } from '../lib/cn';

export interface InputProps extends InputHTMLAttributes<HTMLInputElement> {
  label: string;
  /** Validation message. Presence of this switches the field to its error state. */
  error?: string;
  hint?: string;
  /**
   * A control sitting inside the field's right edge — a password reveal, a unit,
   * a clear button. It overlays the input, so the input also gains right padding
   * to keep text from running underneath it.
   */
  trailing?: ReactNode;
}

/**
 * A labelled text input. The label is required rather than optional, because a
 * placeholder is not a label — it disappears the moment someone types, and screen
 * readers do not reliably announce it.
 */
export const Input = forwardRef<HTMLInputElement, InputProps>(function Input(
  { className, label, error, hint, id, trailing, ...props },
  ref,
) {
  const generatedId = useId();
  const inputId = id ?? generatedId;
  const describedBy = error ? `${inputId}-error` : hint ? `${inputId}-hint` : undefined;

  return (
    <div className="flex flex-col gap-1.5">
      <label htmlFor={inputId} className="text-sm font-medium text-foreground">
        {label}
      </label>

      <div className="relative">
        <input
          ref={ref}
          id={inputId}
          aria-invalid={error ? true : undefined}
          aria-describedby={describedBy}
          className={cn(
            'h-10 w-full rounded-md border bg-background px-3 py-2 text-sm',
            'placeholder:text-muted-foreground',
            'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2',
            'disabled:cursor-not-allowed disabled:opacity-50',
            error ? 'border-destructive' : 'border-input',
            trailing && 'pr-10',
            className,
          )}
          {...props}
        />

        {trailing ? (
          <div className="absolute inset-y-0 right-0 flex items-center pr-1">{trailing}</div>
        ) : null}
      </div>

      {hint && !error ? (
        <p id={`${inputId}-hint`} className="text-xs text-muted-foreground">
          {hint}
        </p>
      ) : null}

      {error ? (
        <p id={`${inputId}-error`} className="text-xs text-destructive" role="alert">
          {error}
        </p>
      ) : null}
    </div>
  );
});
