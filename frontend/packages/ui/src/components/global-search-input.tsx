import type { InputHTMLAttributes } from 'react';
import { cn } from '../lib/cn';
import { SearchIcon } from './icons';

export type GlobalSearchInputProps = Omit<InputHTMLAttributes<HTMLInputElement>, 'type'>;

/**
 * Presentational for now — there is no global search backend yet. It renders
 * and accepts typing like a real input, so it is not a dead click target,
 * but nothing wires its value anywhere until that search exists.
 */
export function GlobalSearchInput({
  className,
  placeholder = 'Search for anything',
  ...props
}: GlobalSearchInputProps) {
  return (
    <label
      className={cn(
        'flex h-12 items-center gap-2 rounded-xl bg-muted px-3 text-sm text-foreground',
        'focus-within:ring-2 focus-within:ring-ring',
        className,
      )}
    >
      <SearchIcon size={16} className="shrink-0 text-muted-foreground" />
      <input
        type="search"
        placeholder={placeholder}
        className="w-full bg-transparent placeholder:text-muted-foreground focus:outline-none"
        {...props}
      />
    </label>
  );
}
