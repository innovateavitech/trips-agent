import { Button } from '@trips/ui';

/**
 * Previous / next, plus a plain sentence saying where you are.
 *
 * No numbered page links: a statement is read by scanning backwards in time or
 * by filtering to a date range, and neither is helped by knowing that page 7
 * exists. The sentence is what makes an export decision possible ("21–40 of
 * 64"), so it is the part that carries weight.
 */
export function StatementPagination({
  page,
  pageSize,
  totalCount,
  onPageChange,
  busy,
}: {
  page: number;
  pageSize: number;
  totalCount: number;
  onPageChange: (page: number) => void;
  busy: boolean;
}) {
  const lastPage = Math.max(1, Math.ceil(totalCount / pageSize));
  const first = totalCount === 0 ? 0 : (page - 1) * pageSize + 1;
  const last = Math.min(page * pageSize, totalCount);

  return (
    <div className="flex flex-wrap items-center justify-between gap-3">
      {/* aria-live so a screen reader hears the range change after paging,
          rather than being silently moved to different rows. */}
      <p className="text-sm text-muted-foreground" aria-live="polite">
        Showing <span className="font-medium text-foreground">{first}</span>–
        <span className="font-medium text-foreground">{last}</span> of{' '}
        <span className="font-medium text-foreground">{totalCount}</span>
      </p>

      <div className="flex gap-2">
        <Button
          variant="outline"
          size="sm"
          disabled={page <= 1 || busy}
          onClick={() => onPageChange(page - 1)}
        >
          Previous
        </Button>
        <Button
          variant="outline"
          size="sm"
          disabled={page >= lastPage || busy}
          onClick={() => onPageChange(page + 1)}
        >
          Next
        </Button>
      </div>
    </div>
  );
}
