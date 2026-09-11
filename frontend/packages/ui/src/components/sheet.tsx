import * as DialogPrimitive from '@radix-ui/react-dialog';
import type { ComponentPropsWithoutRef, ElementRef } from 'react';
import { forwardRef } from 'react';
import { cn } from '../lib/cn';

/**
 * A panel that slides in from the left edge over the page — the agent console's
 * navigation on a tablet, where there is no room for a permanent sidebar.
 *
 * It is a dialog underneath (Radix), which is what makes it behave: focus moves
 * into it and is trapped there, Escape and a tap on the backdrop close it, focus
 * goes back to the menu button afterwards, and the page behind cannot scroll.
 */

export const Sheet = DialogPrimitive.Root;
export const SheetTrigger = DialogPrimitive.Trigger;
export const SheetClose = DialogPrimitive.Close;

export const SheetContent = forwardRef<
  ElementRef<typeof DialogPrimitive.Content>,
  ComponentPropsWithoutRef<typeof DialogPrimitive.Content>
>(function SheetContent({ className, children, ...props }, ref) {
  return (
    <DialogPrimitive.Portal>
      <DialogPrimitive.Overlay className="fixed inset-0 z-40 bg-black/50 data-[state=open]:animate-fade-in motion-reduce:animate-none" />
      <DialogPrimitive.Content
        ref={ref}
        className={cn(
          'fixed inset-y-0 left-0 z-50 flex w-72 max-w-[85vw] flex-col shadow-xl outline-none',
          'data-[state=open]:animate-slide-in-left motion-reduce:animate-none',
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
