import { ArrowDown, ArrowUp, Trash2 } from 'lucide-react';
import type { ReactNode } from 'react';
import { Badge, Button, Card } from '@trips/ui';
import { SECTION_LABELS, type EditorSection as SectionId } from '../catalog-rules';
import type { ProductStatus, PublishProblem } from '../types';

const STATUS_TONE = { Draft: 'info', Published: 'success', Archived: 'neutral' } as const;

export function ProductStatusBadge({ status }: { status: ProductStatus }) {
  return <Badge tone={STATUS_TONE[status]}>{status}</Badge>;
}

/**
 * One part of the editor, as a card the publish checklist can link to. It shows
 * its own publish problems at the top, so the agent sees what is missing where
 * they would fix it.
 */
export function EditorSection({
  id,
  description,
  problems,
  action,
  children,
}: {
  id: SectionId;
  description?: string;
  problems: readonly PublishProblem[];
  action?: ReactNode;
  children: ReactNode;
}) {
  return (
    <Card id={`section-${id}`} className="flex scroll-mt-24 flex-col gap-4 p-5">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h2 className="text-base font-semibold text-foreground">{SECTION_LABELS[id]}</h2>
          {description ? <p className="mt-1 text-sm text-muted-foreground">{description}</p> : null}
        </div>
        {action}
      </div>
      {problems.length > 0 ? (
        <ul className="flex flex-col gap-1 rounded-md bg-warning-subtle p-3 text-sm text-warning-subtle-foreground">
          {problems.map((problem) => (
            <li key={`${problem.field}:${problem.message}`}>{problem.message}</li>
          ))}
        </ul>
      ) : null}
      {children}
    </Card>
  );
}

/**
 * Move up, move down, remove — buttons rather than drag and drop, so they work
 * from a keyboard and on a phone. Inside a disabled fieldset they disable too.
 */
export function RowControls({
  label,
  index,
  count,
  onMove,
  onRemove,
}: {
  label: string;
  index: number;
  count: number;
  onMove: (direction: -1 | 1) => void;
  onRemove: () => void;
}) {
  return (
    <div className="flex shrink-0 items-center gap-1">
      <Button
        type="button"
        variant="ghost"
        size="icon"
        aria-label={`Move ${label} up`}
        disabled={index === 0}
        onClick={() => onMove(-1)}
      >
        <ArrowUp className="h-4 w-4" aria-hidden="true" />
      </Button>
      <Button
        type="button"
        variant="ghost"
        size="icon"
        aria-label={`Move ${label} down`}
        disabled={index === count - 1}
        onClick={() => onMove(1)}
      >
        <ArrowDown className="h-4 w-4" aria-hidden="true" />
      </Button>
      <Button type="button" variant="ghost" size="icon" aria-label={`Remove ${label}`} onClick={onRemove}>
        <Trash2 className="h-4 w-4" aria-hidden="true" />
      </Button>
    </div>
  );
}
