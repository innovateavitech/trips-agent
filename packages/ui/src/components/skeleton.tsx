import type { HTMLAttributes } from 'react';
import { cn } from '../lib/cn';

export type SkeletonProps = HTMLAttributes<HTMLDivElement>;

/**
 * A grey block standing in for content that has not arrived yet.
 *
 * Use it when you know the SHAPE of what is coming — a table of ten rows, a
 * balance card. It keeps the layout from jumping when the data lands, which on
 * a slow Nigerian mobile connection is the difference between "loading" and
 * "broken". When you do not know the shape, use `<Loading />` instead.
 *
 * Marked `aria-hidden` because a screen reader gains nothing from being told
 * about a placeholder rectangle — the surrounding `<Loading />` or `aria-busy`
 * region is what announces the wait.
 */
export function Skeleton({ className, ...props }: SkeletonProps) {
  return (
    <div
      aria-hidden="true"
      className={cn('animate-pulse rounded-md bg-muted', className)}
      {...props}
    />
  );
}
