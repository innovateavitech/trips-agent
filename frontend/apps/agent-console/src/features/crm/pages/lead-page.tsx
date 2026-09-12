import { ArrowLeft } from 'lucide-react';
import { useState, type ReactNode } from 'react';
import { Link, useParams } from 'react-router-dom';
import {
  Button,
  Card,
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogTitle,
  ErrorState,
  LoadingState,
  Select,
  Textarea,
  buttonVariants,
} from '@trips/ui';
import { formatMoneyShort } from '@trips/utils';
import { describeError } from '../../../api/errors';
import { PageHeader } from '../../../shell/page-header';
import { useLead, useMoveLead } from '../crm-api';
import {
  SOURCE_LABEL,
  STAGES,
  describeBudget,
  describeDates,
  describeParty,
  relativeTime,
} from '../crm-rules';
import {
  AddTaskForm,
  LogMessageForm,
  QuoteStatusBadge,
  StageBadge,
  TaskList,
  Timeline,
} from '../components/crm-parts';
import type { Lead, LeadStage } from '../types';

/** Build plan F7 — one lead: the trip they asked for, the quotes that answer it, and every word since. */
export function LeadPage() {
  const { leadId = '' } = useParams();
  const lead = useLead(leadId);

  if (lead.isPending) return <LoadingState size="page" label="Opening the lead" />;
  if (lead.isError) {
    return (
      <ErrorState
        {...describeError(lead.error)}
        onRetry={() => void lead.refetch()}
        retrying={lead.isFetching}
      />
    );
  }

  return <LeadView lead={lead.data} />;
}

function LeadView({ lead }: { lead: Lead }) {
  const move = useMoveLead();
  const [target, setTarget] = useState<LeadStage | ''>('');
  const [losing, setLosing] = useState(false);
  const [reason, setReason] = useState('');

  function moveTo(stage: LeadStage) {
    if (stage === 'Lost') {
      setLosing(true);
      return;
    }
    move.mutate({ id: lead.id, stage, reason: null }, { onSuccess: () => setTarget('') });
  }

  return (
    <div className="flex flex-col gap-6">
      <div>
        <Link
          to="/crm/leads"
          className="inline-flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground"
        >
          <ArrowLeft aria-hidden="true" className="h-4 w-4" />
          Leads
        </Link>
      </div>

      <PageHeader
        title={lead.customer.name}
        description={`${lead.destination} · ${SOURCE_LABEL[lead.source]} · ${relativeTime(lead.createdAt)}`}
        actions={
          <div className="flex flex-wrap items-center gap-2">
            <StageBadge stage={lead.stage} />
            <Link
              to={`/crm/leads/${lead.id}/quotes/new`}
              className={buttonVariants({ size: 'sm' })}
            >
              New quote
            </Link>
          </div>
        }
      />

      {move.isError ? (
        <ErrorState title="We could not move it" detail={describeError(move.error).detail} />
      ) : null}

      <div className="grid items-start gap-6 lg:grid-cols-3">
        <div className="flex flex-col gap-6 lg:col-span-2">
          <Panel title="The trip">
            <dl className="grid gap-4 text-sm sm:grid-cols-2">
              <Fact label="Where">{lead.destination}</Fact>
              <Fact label="When">
                {describeDates(lead.travelFrom, lead.travelTo) || 'No dates yet'}
              </Fact>
              <Fact label="Who">{describeParty(lead.adults, lead.children)}</Fact>
              <Fact label="Budget">
                {describeBudget(lead.budgetMinMinor, lead.budgetMaxMinor, lead.currency)}
              </Fact>
            </dl>
            {lead.message ? (
              <blockquote className="border-l-2 border-primary pl-4 text-sm text-foreground">
                {lead.message}
              </blockquote>
            ) : null}
            {lead.lostReason ? (
              <p className="text-sm text-muted-foreground">Lost: {lead.lostReason}</p>
            ) : null}
          </Panel>

          <Panel title="Quotes">
            {lead.quotes.length === 0 ? (
              <p className="text-sm text-muted-foreground">No quote yet. Answer them with one.</p>
            ) : (
              <ul className="flex flex-col divide-y divide-border rounded-md border border-border">
                {lead.quotes.map((quote) => (
                  <li
                    key={quote.id}
                    className="flex flex-wrap items-center justify-between gap-2 px-4 py-3 text-sm"
                  >
                    <Link
                      to={`/crm/quotes/${quote.id}`}
                      className="font-medium text-foreground underline-offset-4 hover:text-primary hover:underline"
                    >
                      {quote.quoteNumber} · {quote.title}
                    </Link>
                    <span className="flex items-center gap-3">
                      <span className="tabular-nums text-foreground">
                        {formatMoneyShort(quote.totalMinor, quote.currency)}
                      </span>
                      <QuoteStatusBadge status={quote.status} />
                    </span>
                  </li>
                ))}
              </ul>
            )}
          </Panel>

          <Panel title="Messages">
            <LogMessageForm related={{ type: 'Lead', id: lead.id }} />
            <Timeline messages={lead.communications} />
          </Panel>
        </div>

        <aside className="flex flex-col gap-4">
          <Panel title="Stage">
            <div className="flex flex-wrap items-end gap-2">
              <div className="min-w-0 flex-1">
                <Select
                  label="Move to"
                  value={target}
                  onChange={(event) => setTarget(event.target.value as LeadStage | '')}
                >
                  <option value="">Choose a stage</option>
                  {STAGES.filter((stage) => stage.value !== lead.stage).map((stage) => (
                    <option key={stage.value} value={stage.value}>
                      {stage.label}
                    </option>
                  ))}
                </Select>
              </div>
              <Button
                variant="outline"
                disabled={!target}
                loading={move.isPending}
                onClick={() => target && moveTo(target)}
              >
                Move
              </Button>
            </div>
            <ol
              aria-label="Stage history"
              className="flex flex-col gap-2 text-xs text-muted-foreground"
            >
              {[...lead.history].reverse().map((change, index) => (
                <li key={`${change.stage}-${index}`}>
                  <span className="font-medium text-foreground">{change.stage}</span> ·{' '}
                  {change.byName} · {relativeTime(change.at)}
                  {change.reason ? <span> · {change.reason}</span> : null}
                </li>
              ))}
            </ol>
          </Panel>

          <Panel title="Tasks">
            <TaskList tasks={lead.tasks} />
            <AddTaskForm related={{ type: 'Lead', id: lead.id }} />
          </Panel>

          <Panel title="Customer">
            <p className="text-sm text-foreground">{lead.customer.name}</p>
            <p className="text-sm text-muted-foreground">
              {[lead.customer.email, lead.customer.phone].filter(Boolean).join(' · ') ||
                'No contact details'}
            </p>
            <Link
              to={`/crm/customers/${lead.customer.id}`}
              className="text-sm font-medium text-primary underline-offset-4 hover:underline"
            >
              Everything about {lead.customer.name.split(' ')[0]}
            </Link>
          </Panel>
        </aside>
      </div>

      <Dialog open={losing} onOpenChange={(open) => (open ? undefined : setLosing(false))}>
        <DialogContent>
          <DialogTitle>Why was it lost?</DialogTitle>
          <DialogDescription>
            A line is enough: price, dates, they went elsewhere. It is how you learn what to change.
          </DialogDescription>
          <Textarea
            label="Reason"
            rows={3}
            value={reason}
            onChange={(event) => setReason(event.target.value)}
          />
          <DialogFooter>
            <Button variant="outline" onClick={() => setLosing(false)}>
              Not now
            </Button>
            <Button
              disabled={!reason.trim()}
              loading={move.isPending}
              onClick={() =>
                move.mutate(
                  { id: lead.id, stage: 'Lost', reason },
                  {
                    onSuccess: () => {
                      setLosing(false);
                      setReason('');
                      setTarget('');
                    },
                  },
                )
              }
            >
              Mark as lost
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}

function Panel({ title, children }: { title: string; children: ReactNode }) {
  return (
    <Card className="flex flex-col gap-4 p-5">
      <h2 className="text-base font-semibold text-foreground">{title}</h2>
      {children}
    </Card>
  );
}

function Fact({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div>
      <dt className="text-xs text-muted-foreground">{label}</dt>
      <dd className="mt-0.5 text-foreground">{children}</dd>
    </div>
  );
}
