import { formatMoneyShort } from '@trips/utils';
import type { ReactNode } from 'react';
import {
  carrierCounts,
  priceRange,
  stopCounts,
  TIME_OF_DAY_LABELS,
  type FlightFilters,
  type StopsFilter,
  type TimeOfDay,
} from '../search-rules';
import type { FlightOffer } from '../types';

const STOP_OPTIONS: ReadonlyArray<{ value: StopsFilter; label: string }> = [
  { value: 'any', label: 'Any number of stops' },
  { value: 'nonstop', label: 'Direct only' },
  { value: 'one_stop', label: '1 stop or fewer' },
];

const TIMES = Object.keys(TIME_OF_DAY_LABELS) as TimeOfDay[];

/** A hundred positions along the price slider, whatever the spread of fares. */
const PRICE_STEPS = 100;

/**
 * The filters, with a count beside every choice — so an agent can see that
 * "Direct only" leaves two fares before they click it, not after.
 */
export function FlightFilterPanel({
  offers,
  filters,
  onChange,
}: {
  offers: readonly FlightOffer[];
  filters: FlightFilters;
  onChange: (filters: FlightFilters) => void;
}) {
  const stops = stopCounts(offers);
  const carriers = carrierCounts(offers);
  const range = priceRange(offers);
  const currency = offers[0]?.price.currency ?? 'NGN';

  function toggle<T>(list: readonly T[], item: T): T[] {
    return list.includes(item) ? list.filter((entry) => entry !== item) : [...list, item];
  }

  return (
    <div className="flex flex-col gap-6">
      <Group legend="Stops">
        {STOP_OPTIONS.map((option) => (
          <Choice
            key={option.value}
            type="radio"
            name="flight-stops"
            label={option.label}
            count={stops[option.value]}
            checked={filters.stops === option.value}
            onChange={() => onChange({ ...filters, stops: option.value })}
          />
        ))}
      </Group>

      {range && range.max > range.min ? (
        <PriceSlider
          min={range.min}
          max={range.max}
          value={filters.maxPriceMinor}
          currency={currency}
          onChange={(maxPriceMinor) => onChange({ ...filters, maxPriceMinor })}
        />
      ) : null}

      <Group legend="Airlines">
        {carriers.map((carrier) => (
          <Choice
            key={carrier.code}
            type="checkbox"
            label={carrier.name}
            count={carrier.count}
            checked={filters.carriers.includes(carrier.code)}
            onChange={() =>
              onChange({ ...filters, carriers: toggle(filters.carriers, carrier.code) })
            }
          />
        ))}
      </Group>

      <Group legend="Departure time">
        {TIMES.map((time) => (
          <Choice
            key={time}
            type="checkbox"
            label={TIME_OF_DAY_LABELS[time]}
            checked={filters.departureTimes.includes(time)}
            onChange={() =>
              onChange({ ...filters, departureTimes: toggle(filters.departureTimes, time) })
            }
          />
        ))}
      </Group>
    </div>
  );
}

function Group({ legend, children }: { legend: string; children: ReactNode }) {
  return (
    <fieldset className="flex flex-col gap-2">
      <legend className="mb-2 text-sm font-medium text-foreground">{legend}</legend>
      {children}
    </fieldset>
  );
}

function Choice({
  type,
  name,
  label,
  count,
  checked,
  onChange,
}: {
  type: 'radio' | 'checkbox';
  name?: string;
  label: string;
  count?: number;
  checked: boolean;
  onChange: () => void;
}) {
  return (
    <label className="flex cursor-pointer items-center justify-between gap-2 text-sm text-foreground">
      <span className="flex min-w-0 items-center gap-2">
        <input
          type={type}
          name={name}
          checked={checked}
          onChange={onChange}
          className="h-4 w-4 shrink-0 accent-primary"
        />
        <span className="truncate">{label}</span>
      </span>
      {count !== undefined ? (
        <span className="text-xs tabular-nums text-muted-foreground">{count}</span>
      ) : null}
    </label>
  );
}

function PriceSlider({
  min,
  max,
  value,
  currency,
  onChange,
}: {
  min: number;
  max: number;
  value: number | null;
  currency: string;
  onChange: (maxPriceMinor: number | null) => void;
}) {
  const step = Math.max(1, Math.ceil((max - min) / PRICE_STEPS));
  const cap = value ?? max;

  return (
    <fieldset className="flex flex-col gap-2">
      <legend className="mb-2 text-sm font-medium text-foreground">Maximum price</legend>
      <input
        type="range"
        aria-label="Maximum price"
        min={min}
        // Rounded up to a whole step, so the far end of the slider always means "any price".
        max={min + step * PRICE_STEPS}
        step={step}
        value={cap}
        aria-valuetext={value === null ? 'Any price' : `Up to ${formatMoneyShort(cap, currency)}`}
        onChange={(event) => {
          const next = Number(event.target.value);
          onChange(next >= max ? null : next);
        }}
        className="w-full accent-primary"
      />
      <div className="flex justify-between text-xs text-muted-foreground">
        <span className="tabular-nums">{formatMoneyShort(min, currency)}</span>
        <span className="font-medium tabular-nums text-foreground">
          {value === null ? 'Any price' : `Up to ${formatMoneyShort(cap, currency)}`}
        </span>
      </div>
    </fieldset>
  );
}
