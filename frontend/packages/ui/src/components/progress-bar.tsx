import { cn } from '../lib/cn';

export interface ProgressBarSegment {
  /** Share of the whole this segment fills, 0–100. Segments should sum to at most 100. */
  value: number;
  /** A token-backed background class, e.g. "bg-chart-1" — never a raw colour. */
  colorClassName: string;
  label?: string;
}

export interface ProgressBarProps {
  segments: ProgressBarSegment[];
  className?: string;
}

/**
 * A composition bar: each segment is a share of a whole, and the remainder
 * reads as untouched track. This is not a loading indicator — it never
 * animates and it never represents "how much longer", only "how much of".
 */
export function ProgressBar({ segments, className }: ProgressBarProps) {
  const used = segments.reduce((sum, segment) => sum + segment.value, 0);
  const remainder = Math.max(0, 100 - used);

  return (
    <div
      className={cn(
        'flex h-[5px] w-full items-center gap-px overflow-hidden rounded-full',
        className,
      )}
      role="presentation"
    >
      {segments.map((segment, index) => (
        <div
          key={segment.label ?? index}
          className={cn('h-full', segment.colorClassName)}
          style={{ width: `${segment.value}%` }}
          title={segment.label}
        />
      ))}
      {remainder > 0 ? (
        <div className="h-full bg-progress-track" style={{ width: `${remainder}%` }} />
      ) : null}
    </div>
  );
}
