import { useMemo } from 'react';
import { useSearchParams } from 'react-router-dom';
import { Alert, Button, Card, SegmentedControl } from '@trips/ui';
import { Page, PageHeader } from '../../../components/page';
import { RefreshIcon } from '../../../components/icons';
import { EmptyState, ErrorState } from '../../../components/states';
import { describeLoadError } from '../../../lib/api/problem';
import { formatTime } from '../../../lib/format';
import { useDocumentTitle, useNow } from '../../../lib/hooks';
import { QueueTable, QueueTableSkeleton } from '../components/queue-table';
import { useKybQueue } from '../kyb-review-queries';
import {
  QUEUE_FILTERS,
  REVIEW_TARGET_HOURS,
  applyQueueFilter,
  countByFilter,
  emptyFilterCopy,
  parseQueueFilter,
  rankQueue,
  waitingTime,
  type QueueFilter,
} from '../queue-rules';

/** FRD §2.2 UC-1A RS-6, §2.15 — the agencies waiting on Trips, and how long they have waited. */
export function KybQueuePage() {
  useDocumentTitle('KYB review');

  const [params, setParams] = useSearchParams();
  const filter = parseQueueFilter(params.get('status'));
  const queue = useKybQueue();
  const now = useNow();

  const ranked = useMemo(() => rankQueue(queue.data ?? []), [queue.data]);
  const visible = applyQueueFilter(ranked, filter);
  const counts = countByFilter(ranked);
  const overdue = ranked.filter((item) => waitingTime(item.submittedAt, now).overdue).length;

  // The filter lives in the address bar, so a reviewer can reload or come back through the
  // browser's Back button and find the same view.
  const changeFilter = (next: QueueFilter) =>
    setParams(next === 'all' ? {} : { status: next }, { replace: true });

  return (
    <Page>
      <PageHeader
        title="KYB review"
        description="Agencies waiting to be verified, oldest first. Until an agency is approved it cannot fund its wallet or sell anything."
        actions={
          <Button
            variant="outline"
            size="sm"
            onClick={() => void queue.refetch()}
            loading={queue.isFetching}
          >
            {queue.isFetching ? null : <RefreshIcon />}
            Refresh
          </Button>
        }
      />

      {overdue > 0 ? (
        <Alert
          tone="warning"
          title={`${overdue === 1 ? '1 agency has' : `${overdue} agencies have`} waited more than ${REVIEW_TARGET_HOURS} hours`}
        >
          They are at the top of the queue. Every day an agency waits is a day it cannot sell.
        </Alert>
      ) : null}

      <div className="flex flex-wrap items-center justify-between gap-3">
        <SegmentedControl
          label="Filter by status"
          options={QUEUE_FILTERS.map((option) => ({ ...option, count: counts[option.value] }))}
          value={filter}
          onChange={changeFilter}
        />
        {queue.dataUpdatedAt > 0 ? (
          <p className="text-xs text-muted-foreground">
            Updated {formatTime(queue.dataUpdatedAt)}, and every minute.
          </p>
        ) : null}
      </div>

      {queue.isError ? (
        <ErrorState
          {...describeLoadError(queue.error)}
          onRetry={() => void queue.refetch()}
          retrying={queue.isFetching}
        />
      ) : null}

      <Card className="overflow-hidden">
        {queue.isPending ? <QueueTableSkeleton /> : null}

        {queue.data && ranked.length === 0 ? (
          <EmptyState title="The queue is clear">
            No agency is waiting to be verified. New submissions appear here as soon as an agency
            sends its documents.
          </EmptyState>
        ) : null}

        {ranked.length > 0 && visible.length === 0 && filter !== 'all' ? (
          <EmptyState
            title={emptyFilterCopy(filter).title}
            action={
              <Button variant="outline" size="sm" onClick={() => changeFilter('all')}>
                Show everything waiting
              </Button>
            }
          >
            {emptyFilterCopy(filter).detail}
          </EmptyState>
        ) : null}

        {visible.length > 0 ? <QueueTable items={visible} now={now} /> : null}
      </Card>
    </Page>
  );
}
