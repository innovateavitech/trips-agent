import * as DialogPrimitive from '@radix-ui/react-dialog';
import type { ComponentPropsWithoutRef, ElementRef } from 'react';
import { forwardRef } from 'react';
import { cn } from '../lib/cn';

/**
 * A panel that slides in over the page from one edge.
 *
 * `left` (default): the agent console's navigation on a tablet, where there is
 * no room for a permanent sidebar. `right`: a form that belongs to the page
 * behind it — "Add customer" — so the list stays in view as context.
 *
 * It is a dialog underneath (Radix), which is what makes it behave: focus moves
 * into it and is trapped there, Escape and a tap on the backdrop close it, focus
 * goes back to the button that opened it, and the page behind cannot scroll.
 */

export const Sheet = DialogPrimitive.Root;
export const SheetTrigger = DialogPrimitive.Trigger;
export const SheetClose = DialogPrimitive.Close;

export interface SheetContentProps extends ComponentPropsWithoutRef<
  typeof DialogPrimitive.Content
> {
  side?: 'left' | 'right';
}

export const SheetContent = forwardRef<
  ElementRef<typeof DialogPrimitive.Content>,
  SheetContentProps
>(function SheetContent({ className, children, side = 'left', ...props }, ref) {
  return (
    <DialogPrimitive.Portal>
      <DialogPrimitive.Overlay className="fixed inset-0 z-40 bg-black/50 data-[state=open]:animate-fade-in motion-reduce:animate-none" />
      <DialogPrimitive.Content
        ref={ref}
        className={cn(
          'fixed inset-y-0 z-50 flex w-72 max-w-[85vw] flex-col shadow-xl outline-none motion-reduce:animate-none',
          side === 'left'
            ? 'left-0 data-[state=open]:animate-slide-in-left'
            : 'right-0 data-[state=open]:animate-slide-in-right',
          className,
        )}
        {...props}
      >
        {children}
      </DialogPrimitive.Content>
    </DialogPrimitive.Portal>
  );
});

/** Required by Radix for screen readers. Hide it visually with `className="sr-only"` if needed. */
export const SheetTitle = DialogPrimitive.Title;
export const SheetDescription = DialogPrimitive.Description;
