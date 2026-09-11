import * as DialogPrimitive from '@radix-ui/react-dialog';
import type { ComponentPropsWithoutRef, ElementRef, HTMLAttributes } from 'react';
import { forwardRef } from 'react';
import { cn } from '../lib/cn';

/**
 * A modal in the middle of the page, for a decision that has to be made before anything else can
 * happen: accepting a price the airline changed, confirming a refund.
 *
 * Radix underneath, like `Sheet`, which is what makes it behave: focus moves in and is trapped
 * there, Escape and the backdrop close it, focus returns to whatever opened it, and the page behind
 * cannot scroll. Use it sparingly — a modal interrupts, and should only when the answer moves money.
 *
 * Always give it a `DialogTitle`. Radix requires one, and a screen reader announces it on open.
 */
export const Dialog = DialogPrimitive.Root;
export const DialogTrigger = DialogPrimitive.Trigger;
export const DialogClose = DialogPrimitive.Close;

export const DialogContent = forwardRef<
  ElementRef<typeof DialogPrimitive.Content>,
  ComponentPropsWithoutRef<typeof DialogPrimitive.Content>
>(function DialogContent({ className, children, ...props }, ref) {
  return (
    <DialogPrimitive.Portal>
      <DialogPrimitive.Overlay className="fixed inset-0 z-40 bg-black/50 data-[state=open]:animate-fade-in motion-reduce:animate-none" />
      <DialogPrimitive.Content
        ref={ref}
        className={cn(
          // Inset from both edges rather than a fixed width, so it never touches a phone's sides;
          // never taller than the screen, so a phone held sideways can still reach the buttons.
          'fixed inset-x-4 top-1/2 z-50 mx-auto flex max-h-screen max-w-lg -translate-y-1/2 flex-col gap-4 overflow-y-auto',
          'rounded-lg border border-border bg-card p-6 text-card-foreground shadow-xl outline-none',
          'data-[state=open]:animate-fade-in motion-reduce:animate-none',
          className,
        )}
        {...props}
      >
        {children}
      </DialogPrimitive.Content>
    </DialogPrimitive.Portal>
  );
});

export const DialogTitle = forwardRef<
  ElementRef<typeof DialogPrimitive.Title>,
  ComponentPropsWithoutRef<typeof DialogPrimitive.Title>
>(function DialogTitle({ className, ...props }, ref) {
  return (
    <DialogPrimitive.Title
      ref={ref}
      className={cn('text-lg font-semibold text-foreground', className)}
      {...props}
    />
  );
});

export const DialogDescription = forwardRef<
  ElementRef<typeof DialogPrimitive.Description>,
  ComponentPropsWithoutRef<typeof DialogPrimitive.Description>
>(function DialogDescription({ className, ...props }, ref) {
  return (
    <DialogPrimitive.Description
      ref={ref}
      className={cn('text-sm text-muted-foreground', className)}
      {...props}
    />
  );
});

/** The decision itself: stacked on a phone with the confirming action on top, side by side on a desktop. */
export function DialogFooter({ className, ...props }: HTMLAttributes<HTMLDivElement>) {
  return (
    <div
      className={cn('flex flex-col-reverse gap-2 sm:flex-row sm:justify-end', className)}
      {...props}
    />
  );
}
