import { cva } from 'class-variance-authority';
import { cn } from '../lib/cn';

/**
 * One segment, in either of two looks:
 *
 * `track` — selected reads as a raised tab on a sunken track, the convention
 * people already know from every OS. The system default.
 *
 * `pill` — each option is its own fully-rounded button with no shared track;
 * selected is marked by a hairline border and a lifted fill, not by weight or
 * colour (both read the same text colour, on purpose — matches the design
 * this variant was built for).
 *
 * `underline` — a page-level tab strip sitting on its own bottom rule:
 * selected is a primary-coloured label on a primary underline, unselected is
 * muted text with no line. For switching between whole views of a page (the
 * Travel screen's All trips/Active/Upcoming…), not for filtering a list in
 * place — that is what `track` and `pill` are for.
 */
const segmentVariants = cva(
  ['inline-flex items-center gap-2 whitespace-nowrap text-sm transition-colors font-semibold'],
  {
    variants: {
      appearance: {
        track:
          'rounded-md px-3 py-1.5 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring',
        pill: 'h-10 justify-center rounded-full px-4 border border-transparent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring',
        underline:
          'h-11 justify-center border-b-[1.5px] border-transparent px-2.5 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring',
      },
      selected: {
        true: '',
        false: '',
      },
    },
    compoundVariants: [
      {
        appearance: 'track',
        selected: true,
        className: 'bg-background font-medium text-foreground shadow-sm',
      },
      {
        appearance: 'track',
        selected: false,
        className: 'font-medium text-muted-foreground hover:text-foreground',
      },
      {
        appearance: 'pill',
        selected: true,
        className: 'border-border-subtle bg-card text-foreground',
      },
      {
        appearance: 'pill',
        selected: false,
        className: 'bg-transparent text-foreground hover:bg-card/60',
      },
      {
        appearance: 'underline',
        selected: true,
        className: 'border-primary text-primary',
      },
      {
        appearance: 'underline',
        selected: false,
        className: 'font-medium text-muted-foreground hover:text-foreground',
      },
    ],
    defaultVariants: { selected: false, appearance: 'track' },
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
  /**
   * `track` (default): a raised tab on a sunken track. `pill`: independent
   * fully-rounded buttons with no shared track, each the same width — a
   * follow-you-anywhere filter sitting directly on a card, not a form control.
   * `underline`: a page-level tab strip — wrap it in a container with
   * `border-b border-border-subtle` for the shared rule the selected tab's
   * underline sits on.
   */
  appearance?: 'track' | 'pill' | 'underline';
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
  appearance = 'track',
}: SegmentedControlProps<T>) {
  return (
    <div
      role="group"
      aria-label={label}
      className={cn(
        'inline-flex flex-wrap',
        appearance === 'track' && 'gap-1 rounded-lg border border-border bg-muted p-1',
        appearance === 'pill' && 'gap-3',
        appearance === 'underline' && 'gap-1',
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
            className={cn(
              segmentVariants({ selected, appearance }),
              appearance === 'pill' && 'w-25',
            )}
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
