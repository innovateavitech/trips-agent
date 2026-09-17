import {
  Button,
  ChevronDownIcon,
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuTrigger,
  Input,
  buttonVariants,
  cn,
} from '@trips/ui';

/** Two `YYYY-MM-DD` days, either of which may be blank for "no limit". */
export interface DateRange {
  from: string;
  to: string;
}

export const NO_RANGE: DateRange = { from: '', to: '' };

/**
 * A rounded filter pill ("Booking date ⌄") that opens a from/to date picker.
 * The pill turns blue while a range is set, so a hidden filter is never
 * mistaken for an empty list. Shared by the Travel and Customers screens.
 */
export function DateRangeMenu({
  label,
  range,
  onChange,
}: {
  label: string;
  range: DateRange;
  onChange: (range: DateRange) => void;
}) {
  const active = Boolean(range.from || range.to);

  return (
    <DropdownMenu>
      <DropdownMenuTrigger
        className={cn(
          buttonVariants({ variant: 'outline', size: 'sm' }),
          'gap-2 rounded-full',
          active ? 'border-primary text-primary' : 'text-muted-foreground',
        )}
      >
        {label}
        <ChevronDownIcon size={16} />
      </DropdownMenuTrigger>
      <DropdownMenuContent align="start" className="w-64 p-3">
        <div className="flex flex-col gap-3">
          <Input
            type="date"
            label="From"
            value={range.from}
            max={range.to || undefined}
            onChange={(event) => onChange({ ...range, from: event.target.value })}
          />
          <Input
            type="date"
            label="To"
            value={range.to}
            min={range.from || undefined}
            onChange={(event) => onChange({ ...range, to: event.target.value })}
          />
          {active ? (
            <Button type="button" variant="ghost" size="sm" onClick={() => onChange(NO_RANGE)}>
              Clear
            </Button>
          ) : null}
        </div>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
