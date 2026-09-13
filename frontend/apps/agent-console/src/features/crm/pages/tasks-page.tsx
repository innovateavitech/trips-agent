import { useState } from 'react';
import { Button, Card, ErrorState, LoadingState } from '@trips/ui';
import { describeError } from '../../../api/errors';
import { PageHeader } from '../../../shell/page-header';
import { useTasks } from '../crm-api';
import { bucketTasks } from '../crm-rules';
import { TaskList } from '../components/crm-parts';

const SECTIONS = [
  { bucket: 'overdue', title: 'Overdue' },
  { bucket: 'today', title: 'Today' },
  { bucket: 'upcoming', title: 'Coming up' },
] as const;

/** Build plan F7 — the day's follow-ups across every lead and customer, overdue first. */
export function TasksPage() {
  const [showDone, setShowDone] = useState(false);
  const tasks = useTasks(!showDone);
  const buckets = bucketTasks(tasks.data ?? []);

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title="Tasks"
        description="Every follow-up the team has promised, across leads and customers."
        actions={
          <Button variant="outline" size="sm" onClick={() => setShowDone((current) => !current)}>
            {showDone ? 'Hide done' : 'Show done'}
          </Button>
        }
      />

      {tasks.isPending ? <LoadingState label="Loading tasks" /> : null}
      {tasks.isError ? (
        <ErrorState
          {...describeError(tasks.error)}
          onRetry={() => void tasks.refetch()}
          retrying={tasks.isFetching}
        />
      ) : null}

      {tasks.data
        ? [...SECTIONS, ...(showDone ? [{ bucket: 'done', title: 'Done' } as const] : [])].map(
            (section) => (
              <Card key={section.bucket} className="flex flex-col gap-3 p-5">
                <h2 className="text-base font-semibold text-foreground">
                  {section.title}{' '}
                  <span className="font-normal text-muted-foreground">
                    {buckets[section.bucket].length}
                  </span>
                </h2>
                <TaskList tasks={buckets[section.bucket]} showRelated />
              </Card>
            ),
          )
        : null}
    </div>
  );
}
