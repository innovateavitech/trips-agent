import { formatMoneyShort } from '@trips/utils';
import { cx } from '../class-names';
import type { OfferPrice } from '../types';

/**
 * The pieces a flight card and a bus card share: the price, the two ends of a
 * trip, and the line between them. Shared so the two screens read alike.
 */

/**
 * The price, sell first and biggest. Net and margin appear only when the
 * price carries them — and it carries them only for someone with `margin.view`
 * (see `redactFlightResult`), so this component never decides who may see what.
 */
export function PriceBlock({ price, caption }: { price: OfferPrice; caption: string }) {
  return (
    <div className="text-right">
      <p className="text-xs text-muted-foreground">{caption}</p>
      <p className="text-2xl font-semibold tabular-nums tracking-tight text-foreground">
        {formatMoneyShort(price.sellMinor, price.currency)}
      </p>
      {price.margin ? (
        <dl className="mt-1 flex flex-col items-end gap-0.5 text-xs">
          <div className="flex gap-1 text-muted-foreground">
            <dt>Net</dt>
            <dd className="tabular-nums">{formatMoneyShort(price.margin.netMinor, price.currency)}</dd>
          </div>
          <div className="flex gap-1 font-medium text-success-subtle-foreground">
            <dt>Your margin</dt>
            <dd className="tabular-nums">{formatMoneyShort(price.margin.markupMinor, price.currency)}</dd>
          </div>
        </dl>
      ) : null}
    </div>
  );
}

export function Endpoint({
  time,
  place,
  dayShift = 0,
  align = 'start',
}: {
  time: string;
  place: string;
  dayShift?: number;
  align?: 'start' | 'end';
}) {
  return (
    <div className={cx('shrink-0', align === 'end' ? 'text-right' : 'text-left')}>
      <p className="text-lg font-semibold leading-tight tabular-nums text-foreground">
        {time}
        {dayShift > 0 ? (
          <sup className="ml-0.5 text-xs font-medium text-warning-subtle-foreground">
            +{dayShift}
            <span className="sr-only"> {dayShift === 1 ? 'day' : 'days'} later</span>
          </sup>
        ) : null}
      </p>
      <p className="text-xs text-muted-foreground">{place}</p>
    </div>
  );
}

/** The line between departure and arrival: how long, and whether it stops. */
export function Timeline({
  duration,
  caption,
  stops = 0,
}: {
  duration: string;
  caption: string;
  stops?: number;
}) {
  return (
    <div className="flex min-w-0 flex-1 flex-col items-center gap-1">
      <span className="text-xs tabular-nums text-muted-foreground">{duration}</span>
      <div className="flex w-full items-center gap-1" aria-hidden="true">
        <span className="h-1.5 w-1.5 shrink-0 rounded-full bg-muted-foreground" />
        <span className="h-px flex-1 bg-border" />
        {Array.from({ length: stops }, (_, index) => (
          <span key={index} className="flex flex-1 items-center gap-1">
            <span className="h-2 w-2 shrink-0 rounded-full border-2 border-warning bg-background" />
            <span className="h-px flex-1 bg-border" />
          </span>
        ))}
        <span className="h-1.5 w-1.5 shrink-0 rounded-full bg-muted-foreground" />
      </div>
      <span
        className={cx(
          'truncate text-xs',
          stops === 0 ? 'text-success-subtle-foreground' : 'text-warning-subtle-foreground',
        )}
      >
        {caption}
      </span>
    </div>
  );
}
