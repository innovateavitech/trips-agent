import { Skeleton } from '@trips/ui';

/**
 * The pieces the Figma list screens (Travel, Customers) share, so the lists
 * cannot drift apart one screen at a time.
 */

/**
 * The grey, rounded header row. Put it on every `TableHead` so the first and
 * last cells round the row's ends.
 */
export const LIST_HEADER_CELL =
  'h-10 bg-muted text-xs font-medium normal-case tracking-normal text-muted-foreground first:rounded-l-lg last:rounded-r-lg';

/** Five placeholder rows while a list loads. `label` is read out, e.g. "Loading trips". */
export function ListSkeleton({ label }: { label: string }) {
  return (
    <div
      aria-busy="true"
      aria-label={label}
      className="flex flex-col divide-y divide-border-subtle overflow-hidden rounded-xl border border-border-subtle"
    >
      {[0, 1, 2, 3, 4].map((row) => (
        <div key={row} className="flex items-center gap-4 px-4 py-4">
          <Skeleton className="h-10 w-10 rounded-full" />
          <Skeleton className="h-4 flex-1" />
          <Skeleton className="h-4 w-24" />
          <Skeleton className="h-4 w-20" />
        </div>
      ))}
    </div>
  );
}
