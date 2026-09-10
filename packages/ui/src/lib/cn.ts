import { clsx, type ClassValue } from 'clsx';
import { twMerge } from 'tailwind-merge';

/**
 * Merges class names, with later Tailwind classes correctly overriding earlier
 * conflicting ones. `cn('p-2', 'p-4')` gives `p-4`, not both.
 *
 * Use this in every component that accepts a `className` prop, so a caller can
 * adjust spacing without fighting specificity.
 */
export function cn(...inputs: ClassValue[]): string {
  return twMerge(clsx(inputs));
}
