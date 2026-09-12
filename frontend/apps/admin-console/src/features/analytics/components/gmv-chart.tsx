import { formatMoney } from '../../../lib/format';
import { formatDay } from '../analytics-rules';
import type { PlatformDay } from '../types';

/**
 * GMV by day, as bars.
 *
 * Inline SVG in the console's own style: a 100-unit grid, `currentColor` nowhere and token classes
 * everywhere, so the chart follows the theme exactly as the icons do. One series only — GMV and
 * fee revenue differ by two orders of magnitude, and drawing both would make the smaller one a
 * flat line pretending to be zero.
 */
export function GmvChart({
  days,
  currency,
  withYear,
}: {
  days: PlatformDay[];
  currency: string;
  withYear: boolean;
}) {
  if (days.length === 0) {
    return (
      <p className="text-sm text-muted-foreground">
        Nothing was sold anywhere on the platform in this window.
      </p>
    );
  }

  const peak = Math.max(...days.map((day) => day.gmvMinor), 1);
  const total = days.reduce((sum, day) => sum + day.gmvMinor, 0);

  const slot = 10;
  const barWidth = 8;
  const height = 100;
  const width = days.length * slot;

  return (
    <figure className="flex flex-col gap-2">
      <svg
        viewBox={`0 0 ${width} ${height}`}
        preserveAspectRatio="none"
        className="h-44 w-full"
        role="img"
        aria-label={`Gross merchandise value by day, ${formatMoney(total, currency)} in total across the window. The busiest day is ${formatMoney(peak, currency)}.`}
      >
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
          // A day with any sales always gets a visible sliver, so "quiet" and "nothing at all"
          // are not drawn the same way.
          const scaled = day.gmvMinor === 0 ? 0 : Math.max(1, (day.gmvMinor / peak) * height);

          return (
            <rect
              key={day.day}
              x={index * slot + (slot - barWidth) / 2}
              y={height - scaled}
              width={barWidth}
              height={scaled}
              rx="1"
              className="fill-primary"
            >
              <title>{`${formatDay(day.day, withYear)}: ${formatMoney(day.gmvMinor, currency)} from ${day.bookings} bookings`}</title>
            </rect>
          );
        })}
      </svg>

      <figcaption className="flex items-center justify-between text-xs text-muted-foreground">
        <span>{formatDay(days[0]!.day, withYear)}</span>
        <span>Busiest day {formatMoney(peak, currency)}</span>
        <span>{formatDay(days[days.length - 1]!.day, withYear)}</span>
      </figcaption>
    </figure>
  );
}
