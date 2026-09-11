import { useState, type FormEvent } from 'react';
import { Link } from 'react-router-dom';
import { Badge, Button, Input, Select, Textarea } from '@trips/ui';
import { describeError } from '../../../api/errors';
import { cx } from '../../search/class-names';
import { useAddTask, useCompleteTask, useLogCommunication } from '../crm-api';
import { CHANNEL_LABEL, QUOTE_STATUS_TONE, STAGE_TONE, bucketOf, relativeTime } from '../crm-rules';
import type {
  Channel,
  Communication,
  Direction,
  LeadStage,
  QuoteStatus,
  RelatedType,
  Task,
} from '../types';

export function StageBadge({ stage }: { stage: LeadStage }) {
  return <Badge tone={STAGE_TONE[stage]}>{stage}</Badge>;
}

export function QuoteStatusBadge({ status }: { status: QuoteStatus }) {
  return <Badge tone={QUOTE_STATUS_TONE[status]}>{status}</Badge>;
}

export function relatedPath(related: { type: RelatedType; id: string }): string {
  if (related.type === 'Lead') return `/crm/leads/${related.id}`;
  if (related.type === 'Quote') return `/crm/quotes/${related.id}`;
  return `/crm/customers/${related.id}`;
}

/** Tasks with a checkbox each: ticking one marks it done. Overdue ones say so in red. */
export function TaskList({ tasks, showRelated = false }: { tasks: Task[]; showRelated?: boolean }) {
  const complete = useCompleteTask();

  if (tasks.length === 0) return <p className="text-sm text-muted-foreground">Nothing to do.</p>;

  return (
    <ul className="flex flex-col divide-y divide-border rounded-md border border-border">
      {tasks.map((task) => {
        const bucket = bucketOf(task);
        const done = task.completedAt !== null;

        return (
          <li key={task.id} className="flex items-start gap-3 px-4 py-3">
            <input
              type="checkbox"
              aria-label={`Done: ${task.title}`}
              className="mt-1 h-4 w-4 accent-primary"
              checked={done}
              disabled={done || complete.isPending}
              onChange={() => complete.mutate(task.id)}
            />
            <div className="min-w-0 flex-1">
              <p
                className={cx(
                  'text-sm',
                  done ? 'text-muted-foreground line-through' : 'text-foreground',
                )}
              >
                {task.title}
              </p>
              <p
                className={cx(
                  'text-xs',
                  bucket === 'overdue' ? 'text-destructive' : 'text-muted-foreground',
                )}
              >
                {done && task.completedAt
                  ? `Done ${relativeTime(task.completedAt)}`
                  : bucket === 'overdue'
                    ? `Overdue: it was due ${relativeTime(task.dueAt)}`
                    : `Due ${relativeTime(task.dueAt)}`}
                {showRelated ? (
                  <>
                    {' · '}
                    <Link
                      to={relatedPath(task.related)}
                      className="underline-offset-4 hover:underline"
                    >
                      {task.related.label}
                    </Link>
                  </>
                ) : null}
              </p>
            </div>
          </li>
        );
      })}
    </ul>
  );
}

/** Tomorrow at ten, Lagos time, as a `datetime-local` value. */
function tomorrowAtTen(): string {
  const day = new Intl.DateTimeFormat('en-CA', { timeZone: 'Africa/Lagos' }).format(
    new Date(Date.now() + 86_400_000),
  );
  return `${day}T10:00`;
}

export function AddTaskForm({ related }: { related: { type: RelatedType; id: string } }) {
  const add = useAddTask();
  const [title, setTitle] = useState('');
  const [due, setDue] = useState(tomorrowAtTen);

  function submit(event: FormEvent) {
    event.preventDefault();
    if (!title.trim() || !due) return;
    // The picker's time is the agent's wall clock, and the agent is in Lagos.
    add.mutate(
      { title, dueAt: new Date(`${due}:00+01:00`).toISOString(), related },
      { onSuccess: () => setTitle('') },
    );
  }

  return (
    <form onSubmit={submit} aria-label="Add a task" className="flex flex-wrap items-end gap-2">
      <div className="min-w-0 flex-1 basis-48">
        <Input
          label="New task"
          placeholder="Call back about hotels"
          value={title}
          onChange={(event) => setTitle(event.target.value)}
          error={add.isError ? describeError(add.error).title : undefined}
        />
      </div>
      <div className="w-52">
        <Input
          type="datetime-local"
          label="Due"
          value={due}
          onChange={(event) => setDue(event.target.value)}
        />
      </div>
      <Button type="submit" variant="outline" loading={add.isPending} disabled={!title.trim()}>
        Add
      </Button>
    </form>
  );
}

/** Every call, message and note about a lead or customer, newest first. */
export function Timeline({ messages }: { messages: Communication[] }) {
  if (messages.length === 0)
    return <p className="text-sm text-muted-foreground">No messages yet.</p>;

  return (
    <ol aria-label="Messages" className="flex flex-col gap-4">
      {messages.map((message) => (
        <li key={message.id} className="flex gap-3">
          <span aria-hidden="true" className="mt-1.5 h-2 w-2 shrink-0 rounded-full bg-primary" />
          <div className="min-w-0">
            <p className="text-sm text-foreground">{message.summary}</p>
            <p className="text-xs text-muted-foreground">
              {CHANNEL_LABEL[message.channel]} ·{' '}
              {message.direction === 'Inbound' ? `from ${message.byName}` : `by ${message.byName}`}{' '}
              · {relativeTime(message.at)}
            </p>
          </div>
        </li>
      ))}
    </ol>
  );
}

export function LogMessageForm({ related }: { related: { type: RelatedType; id: string } }) {
  const log = useLogCommunication();
  const [channel, setChannel] = useState<Channel>('Call');
  const [direction, setDirection] = useState<Direction>('Outbound');
  const [summary, setSummary] = useState('');

  function submit(event: FormEvent) {
    event.preventDefault();
    if (!summary.trim()) return;
    log.mutate({ channel, direction, summary, related }, { onSuccess: () => setSummary('') });
  }

  return (
    <form onSubmit={submit} aria-label="Log a message" className="flex flex-col gap-3">
      <Textarea
        label="What was said"
        rows={2}
        value={summary}
        onChange={(event) => setSummary(event.target.value)}
        error={log.isError ? describeError(log.error).title : undefined}
      />
      <div className="flex flex-wrap items-end gap-2">
        <div className="w-36">
          <Select
            label="How"
            value={channel}
            onChange={(event) => setChannel(event.target.value as Channel)}
          >
            {(Object.keys(CHANNEL_LABEL) as Channel[]).map((value) => (
              <option key={value} value={value}>
                {CHANNEL_LABEL[value]}
              </option>
            ))}
          </Select>
        </div>
        <div className="w-52">
          <Select
            label="Who started it"
            value={direction}
            onChange={(event) => setDirection(event.target.value as Direction)}
          >
            <option value="Outbound">We contacted them</option>
            <option value="Inbound">They contacted us</option>
          </Select>
        </div>
        <Button type="submit" variant="outline" loading={log.isPending} disabled={!summary.trim()}>
          Log it
        </Button>
      </div>
    </form>
  );
}
