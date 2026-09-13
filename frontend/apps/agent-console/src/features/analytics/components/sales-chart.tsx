import { formatMoney } from '@trips/utils';
import { formatDay } from '../analytics-rules';
import type { AnalyticsDay } from '../types';

/**
 * Daily gross sales, as bars.
 *
 * Inline SVG rather than a charting library: one series of at most a few
 * hundred bars needs no dependency, and every library would have to be taught
 * the design tokens anyway. Colour comes from `fill-primary` and
 * `stroke-border`, so the chart follows the theme — including dark mode —
 * without knowing a single colour value.
 *
 * The chart is `role="img"` with a sentence describing it, and the same numbers
 * are in the table beneath it, so nothing here is the only way to reach a
 * figure.
 */
export function SalesChart({
  days,
  currency,
  withYear,
  onSelectDay,
}: {
  days: AnalyticsDay[];
  currency: string;
  withYear: boolean;
  onSelectDay?: (day: string) => void;
}) {
  if (days.length === 0) return null;

  const peak = Math.max(...days.map((day) => day.grossSalesMinor), 1);
  const total = days.reduce((sum, day) => sum + day.grossSalesMinor, 0);

  // A viewBox in abstract units, scaled by CSS. The bars keep their proportions
  // at any width, and one bar's slot is always 10 units wide with a 2-unit gap.
  const slot = 10;
  const barWidth = 8;
  const height = 100;
  const width = days.length * slot;

  return (
    <figure className="flex flex-col gap-3">
      <svg
        viewBox={`0 0 ${width} ${height}`}
        preserveAspectRatio="none"
        className="h-48 w-full"
        role="img"
        aria-label={`Gross sales by day, ${formatMoney(total, currency)} in total. The tallest day is ${formatMoney(peak, currency)}.`}
      >
        {/* The baseline. Without it a run of zero-sales days reads as missing data. */}
        <line
          x1="0"
          y1={height}
          x2={width}
          y2={height}
          className="stroke-border"
          strokeWidth="0.5"
          vectorEffect="non-scaling-stroke"
        />

        {days.map((day, index) => {
          // A day that sold something always gets at least a sliver, so "a
          // small sale" and "no sale at all" are visibly different.
          const scaled =
            day.grossSalesMinor === 0 ? 0 : Math.max(1, (day.grossSalesMinor / peak) * height);

          return (
            <g key={day.day}>
              <rect
                x={index * slot + (slot - barWidth) / 2}
                y={height - scaled}
                width={barWidth}
                height={scaled}
                rx="1"
                className="fill-primary"
              />

              {/* A transparent full-height target, so a low bar is still easy to
                  hit with a mouse and reachable with a keyboard. */}
              <rect
                x={index * slot}
                y="0"
                width={slot}
                height={height}
                className="cursor-pointer fill-transparent focus-visible:fill-primary-subtle"
                tabIndex={onSelectDay ? 0 : -1}
                role={onSelectDay ? 'button' : undefined}
                aria-label={`${formatDay(day.day, withYear)}: ${formatMoney(day.grossSalesMinor, currency)} from ${day.bookings} ${day.bookings === 1 ? 'booking' : 'bookings'}`}
                onClick={() => onSelectDay?.(day.day)}
                onKeyDown={(event) => {
                  if (event.key === 'Enter' || event.key === ' ') {
                    event.preventDefault();
                    onSelectDay?.(day.day);
                  }
                }}
              >
                <title>
                  {`${formatDay(day.day, withYear)}: ${formatMoney(day.grossSalesMinor, currency)}`}
                </title>
              </rect>
            </g>
          );
        })}
      </svg>

      <figcaption className="flex items-center justify-between text-xs text-muted-foreground">
        <span>{formatDay(days[0]!.day, withYear)}</span>
        <span>Peak {formatMoney(peak, currency)}</span>
        <span>{formatDay(days[days.length - 1]!.day, withYear)}</span>
      </figcaption>
    </figure>
  );
}
