import * as DropdownMenuPrimitive from '@radix-ui/react-dropdown-menu';
import type { ComponentPropsWithoutRef, ElementRef } from 'react';
import { forwardRef } from 'react';
import { cn } from '../lib/cn';

/**
 * A menu that opens from a button: the user menu, the agency switcher.
 *
 * Built on Radix (the primitive shadcn/ui uses) rather than by hand, because a
 * menu that is actually accessible is a lot of quiet work — arrow-key movement,
 * typeahead, Escape to close, focus returned to the trigger, the right ARIA
 * roles — and every hand-rolled one gets some of it wrong. Radix renders the
 * content in a portal, so it is never clipped by an `overflow: hidden` parent.
 *
 * Only the look lives here; behaviour is Radix's.
 */

export const DropdownMenu = DropdownMenuPrimitive.Root;
export const DropdownMenuTrigger = DropdownMenuPrimitive.Trigger;
export const DropdownMenuGroup = DropdownMenuPrimitive.Group;
export const DropdownMenuRadioGroup = DropdownMenuPrimitive.RadioGroup;

export const DropdownMenuContent = forwardRef<
  ElementRef<typeof DropdownMenuPrimitive.Content>,
  ComponentPropsWithoutRef<typeof DropdownMenuPrimitive.Content>
>(function DropdownMenuContent({ className, sideOffset = 6, ...props }, ref) {
  return (
    <DropdownMenuPrimitive.Portal>
      <DropdownMenuPrimitive.Content
        ref={ref}
        sideOffset={sideOffset}
        className={cn(
          'z-50 min-w-56 overflow-hidden rounded-lg border border-border bg-popover p-1 text-popover-foreground shadow-lg',
          'data-[state=open]:animate-fade-in motion-reduce:animate-none',
          className,
        )}
        {...props}
      />
    </DropdownMenuPrimitive.Portal>
  );
});

const itemClasses = [
  'relative flex cursor-default select-none items-center gap-2 rounded-md px-2 py-2 text-sm outline-none',
  'transition-colors focus:bg-accent focus:text-accent-foreground',
  'data-[disabled]:pointer-events-none data-[disabled]:opacity-50',
];

export const DropdownMenuItem = forwardRef<
  ElementRef<typeof DropdownMenuPrimitive.Item>,
  ComponentPropsWithoutRef<typeof DropdownMenuPrimitive.Item>
>(function DropdownMenuItem({ className, ...props }, ref) {
  return <DropdownMenuPrimitive.Item ref={ref} className={cn(itemClasses, className)} {...props} />;
});

/**
 * One choice of several, with a tick on the current one — the agency switcher.
 * A radio item rather than a plain item so a screen reader says "checked".
 */
export const DropdownMenuRadioItem = forwardRef<
  ElementRef<typeof DropdownMenuPrimitive.RadioItem>,
  ComponentPropsWithoutRef<typeof DropdownMenuPrimitive.RadioItem>
>(function DropdownMenuRadioItem({ className, children, ...props }, ref) {
  return (
    <DropdownMenuPrimitive.RadioItem
      ref={ref}
      className={cn(itemClasses, 'pr-8', className)}
      {...props}
    >
      {children}
      <span className="absolute right-2 flex h-4 w-4 items-center justify-center text-primary">
        <DropdownMenuPrimitive.ItemIndicator>
          <svg
            viewBox="0 0 16 16"
            className="h-4 w-4"
            fill="none"
            stroke="currentColor"
            strokeWidth="2"
            strokeLinecap="round"
            strokeLinejoin="round"
            aria-hidden="true"
          >
            <path d="M3.5 8.5l3 3 6-7" />
          </svg>
        </DropdownMenuPrimitive.ItemIndicator>
      </span>
    </DropdownMenuPrimitive.RadioItem>
  );
});

export const DropdownMenuLabel = forwardRef<
  ElementRef<typeof DropdownMenuPrimitive.Label>,
  ComponentPropsWithoutRef<typeof DropdownMenuPrimitive.Label>
>(function DropdownMenuLabel({ className, ...props }, ref) {
  return (
    <DropdownMenuPrimitive.Label
      ref={ref}
      className={cn('px-2 py-1.5 text-xs font-medium text-muted-foreground', className)}
      {...props}
    />
  );
});

export const DropdownMenuSeparator = forwardRef<
  ElementRef<typeof DropdownMenuPrimitive.Separator>,
  ComponentPropsWithoutRef<typeof DropdownMenuPrimitive.Separator>
>(function DropdownMenuSeparator({ className, ...props }, ref) {
  return (
    <DropdownMenuPrimitive.Separator
      ref={ref}
      className={cn('-mx-1 my-1 h-px bg-border', className)}
      {...props}
    />
  );
});
