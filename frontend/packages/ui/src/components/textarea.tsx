import type { TextareaHTMLAttributes } from 'react';
import { forwardRef, useId } from 'react';
import { cn } from '../lib/cn';

export interface TextareaProps extends TextareaHTMLAttributes<HTMLTextAreaElement> {
  label: string;
  /** Validation message. Presence of this switches the field to its error state. */
  error?: string;
  hint?: string;
}

/**
 * A labelled multi-line text field — for writing a sentence or two, such as the reason a KYB
 * submission was turned down.
 *
 * Same anatomy and states as `Input`, on purpose: a form that mixes the two should not look like
 * it was assembled from two kits. The label is required for the same reason as there.
 */
export const Textarea = forwardRef<HTMLTextAreaElement, TextareaProps>(function Textarea(
  { className, label, error, hint, id, rows = 4, ...props },
  ref,
) {
  const generatedId = useId();
  const fieldId = id ?? generatedId;
  const describedBy = error ? `${fieldId}-error` : hint ? `${fieldId}-hint` : undefined;

  return (
    <div className="flex flex-col gap-1.5">
      <label htmlFor={fieldId} className="text-sm font-medium text-foreground">
        {label}
      </label>

      <textarea
        ref={ref}
        id={fieldId}
        rows={rows}
        aria-invalid={error ? true : undefined}
        aria-describedby={describedBy}
        className={cn(
          'w-full resize-y rounded-md border bg-background px-3 py-2 text-sm leading-relaxed',
          'placeholder:text-muted-foreground',
          'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2',
          'disabled:cursor-not-allowed disabled:opacity-50',
          error ? 'border-destructive' : 'border-input',
          className,
        )}
        {...props}
      />

      {hint && !error ? (
        <p id={`${fieldId}-hint`} className="text-xs text-muted-foreground">
          {hint}
        </p>
      ) : null}

      {error ? (
        <p id={`${fieldId}-error`} className="text-xs text-destructive" role="alert">
          {error}
        </p>
      ) : null}
    </div>
  );
});
