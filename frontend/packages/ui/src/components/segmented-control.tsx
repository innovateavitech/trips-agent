import { cva } from 'class-variance-authority';
import { cn } from '../lib/cn';

/**
 * One segment. Selected reads as a raised tab on a sunken track, which is the convention people
 * already know from every OS — familiarity beats invention for a control used forty times a day.
 */
const segmentVariants = cva(
  [
    'inline-flex items-center gap-2 whitespace-nowrap rounded-md px-3 py-1.5 text-sm transition-colors',
    'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring',
  ],
  {
    variants: {
      selected: {
        true: 'bg-background font-medium text-foreground shadow-sm',
        false: 'text-muted-foreground hover:text-foreground',
      },
    },
    defaultVariants: { selected: false },
  },
);

export interface SegmentedOption<T extends string> {
  value: T;
  label: string;
  /** Shown after the label — how many items choosing this option would show. */
  count?: number;
}

export interface SegmentedControlProps<T extends string> {
  /** Names the group for screen readers, e.g. "Filter by status". */
  label: string;
  options: ReadonlyArray<SegmentedOption<T>>;
  value: T;
  onChange: (value: T) => void;
  className?: string;
}

/**
 * Pick one of a few mutually exclusive views: a status filter, a period toggle.
 *
 * Buttons with `aria-pressed` rather than radio inputs: each segment acts immediately and has no
 * "submit", which is how a toggle button behaves. For more than five or six options, use `Select`.
 */
export function SegmentedControl<T extends string>({
  label,
  options,
  value,
  onChange,
  className,
}: SegmentedControlProps<T>) {
  return (
    <div
      role="group"
      aria-label={label}
      className={cn(
        'inline-flex flex-wrap gap-1 rounded-lg border border-border bg-muted p-1',
        className,
      )}
    >
      {options.map((option) => {
        const selected = option.value === value;

        return (
          <button
            key={option.value}
            type="button"
            aria-pressed={selected}
            onClick={() => onChange(option.value)}
            className={segmentVariants({ selected })}
          >
            {option.label}
            {option.count !== undefined ? (
              <span
                className={cn(
                  'text-xs tabular-nums',
                  selected ? 'text-primary' : 'text-muted-foreground',
                )}
              >
                {option.count}
              </span>
            ) : null}
          </button>
        );
      })}
    </div>
  );
}

export { segmentVariants };
