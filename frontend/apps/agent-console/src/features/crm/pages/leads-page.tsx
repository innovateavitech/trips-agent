import { useState } from 'react';
import { Link } from 'react-router-dom';
import { ErrorState, Input, LoadingState, buttonVariants } from '@trips/ui';
import { formatMoneyShort } from '@trips/utils';
import { describeError } from '../../../api/errors';
import { PageHeader } from '../../../shell/page-header';
import { cx } from '../../search/class-names';
import { useLeads } from '../crm-api';
import {
  SOURCE_LABEL,
  STAGES,
  describeTrip,
  groupByStage,
  relativeTime,
  searchLeads,
} from '../crm-rules';
import type { LeadSummary } from '../types';

/**
 * Build plan F7 — the pipeline: every lead in the column for where it stands,
 * newest first. New ones from the website land in "New" on their own.
 */
export function LeadsPage() {
  const leads = useLeads();
  const [query, setQuery] = useState('');
  const columns = groupByStage(searchLeads(leads.data ?? [], query));

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title="Leads"
        description="Trip requests from your website and the ones you add, from first message to booked."
        actions={
          <Link to="/crm/leads/new" className={buttonVariants({ size: 'sm' })}>
            New lead
          </Link>
        }
      />

      <div className="max-w-md">
        <Input
          type="search"
          label="Search"
          placeholder="Name, email, phone or destination"
          value={query}
          onChange={(event) => setQuery(event.target.value)}
        />
      </div>

      {leads.isPending ? <LoadingState label="Loading your leads" /> : null}
      {leads.isError ? (
        <ErrorState
          {...describeError(leads.error)}
          onRetry={() => void leads.refetch()}
          retrying={leads.isFetching}
        />
      ) : null}

      {leads.data ? (
        <div className="overflow-x-auto pb-2">
          <div className="flex gap-4">
            {STAGES.map((stage) => (
              <section
                key={stage.value}
                aria-label={`${stage.label} leads`}
                className="flex w-72 shrink-0 flex-col gap-3 rounded-lg bg-muted p-3"
              >
                <div>
                  <h2 className="text-sm font-semibold text-foreground">
                    {stage.label}{' '}
                    <span className="font-normal text-muted-foreground">
                      {columns[stage.value].length}
                    </span>
                  </h2>
                  <p className="text-xs text-muted-foreground">{stage.hint}</p>
                </div>
                {columns[stage.value].length === 0 ? (
                  <p className="rounded-md border border-dashed border-border p-4 text-center text-xs text-muted-foreground">
                    None
                  </p>
                ) : (
                  columns[stage.value].map((lead) => <LeadCard key={lead.id} lead={lead} />)
                )}
              </section>
            ))}
          </div>
        </div>
      ) : null}
    </div>
  );
}

function LeadCard({ lead }: { lead: LeadSummary }) {
  const overdue = lead.nextTaskDueAt !== null && new Date(lead.nextTaskDueAt) < new Date();

  return (
    <Link
      to={`/crm/leads/${lead.id}`}
      className="block rounded-lg border border-border bg-card p-3 hover:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
    >
      <p className="text-sm font-medium text-foreground">{lead.customer.name}</p>
      <p className="mt-1 text-xs text-muted-foreground">{describeTrip(lead)}</p>
      <p className="mt-2 text-xs text-muted-foreground">
        {lead.budgetMaxMinor !== null
          ? `Up to ${formatMoneyShort(lead.budgetMaxMinor, lead.currency)} · `
          : ''}
        {SOURCE_LABEL[lead.source]} · {relativeTime(lead.createdAt)}
      </p>
      {lead.nextTaskDueAt ? (
        <p
          className={cx(
            'mt-2 text-xs font-medium',
            overdue ? 'text-destructive' : 'text-foreground',
          )}
        >
          {overdue ? 'A task is overdue' : `Next task ${relativeTime(lead.nextTaskDueAt)}`}
        </p>
      ) : lead.stage === 'New' ? (
        <p className="mt-2 text-xs font-medium text-warning-subtle-foreground">
          Nobody has answered yet
        </p>
      ) : null}
    </Link>
  );
}
