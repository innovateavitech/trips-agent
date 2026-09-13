import { useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { Button, Card, Input, Select } from '@trips/ui';
import { Page, PageHeader } from '../../../components/page';
import { RefreshIcon } from '../../../components/icons';
import { EmptyState, ErrorState } from '../../../components/states';
import { describeLoadError } from '../../../lib/api/problem';
import { useDocumentTitle } from '../../../lib/hooks';
import { AuditTable, AuditTableSkeleton } from '../components/audit-table';
import { EntryDetail } from '../components/entry-detail';
import { actionLabel } from '../audit-rules';
import { useAuditActions, useAuditLog } from '../audit-queries';
import type { AuditLogEntry } from '../types';

const PAGE_SIZE = 50;

/**
 * The audit viewer: who did what to whom, and why (FRD §2.15 RS-5).
 *
 * Filters live in the address bar, like the directory's, so "every suspension last month" is a
 * link somebody can send rather than a set of dropdowns they have to describe over a call.
 *
 * There is no auto-refresh here, deliberately. This screen is read while investigating something,
 * and a table that reorders itself under a person part way through reading a row is worse than a
 * table that is thirty seconds out of date. The refresh button is right there.
 */
export function AuditLogPage() {
  useDocumentTitle('Audit log');

  const [params, setParams] = useSearchParams();
  const [selected, setSelected] = useState<AuditLogEntry | null>(null);

  const agencyId = params.get('agencyId') ?? '';
  const action = params.get('action') ?? '';
  const entityType = params.get('entityType') ?? '';
  const from = params.get('from') ?? '';
  const to = params.get('to') ?? '';
  const page = Math.max(1, Number(params.get('page') ?? '1') || 1);

  const trail = useAuditLog({
    agencyId: agencyId || undefined,
    action: action || undefined,
    entityType: entityType || undefined,
    from: toInstant(from),
    to: toInstant(to, true),
    page,
    pageSize: PAGE_SIZE,
  });

  const actions = useAuditActions();

  function change(key: string, value: string, resetPage = true) {
    const next = new URLSearchParams(params);

    if (value === '') next.delete(key);
    else next.set(key, value);

    if (resetPage) next.delete('page');

    setParams(next, { replace: true });
  }

  const total = trail.data?.totalCount ?? 0;
  const lastPage = Math.max(1, Math.ceil(total / PAGE_SIZE));
  const filtered = Boolean(agencyId || action || entityType || from || to);

  return (
    <Page wide>
      <PageHeader
        title="Audit log"
        description="Every recorded action across the platform, with the reason given for it."
        actions={
          <Button
            variant="outline"
            size="sm"
            onClick={() => void trail.refetch()}
            loading={trail.isFetching}
          >
            {trail.isFetching ? null : <RefreshIcon />}
            Refresh
          </Button>
        }
      />

      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <Select
          label="Action"
          value={action}
          onChange={(event) => change('action', event.target.value)}
          hint="Read from the trail itself, so a new action appears here the day it is first written."
        >
          <option value="">Any action</option>
          {(actions.data ?? []).map((name) => (
            <option key={name} value={name}>
              {actionLabel(name)}
            </option>
          ))}
        </Select>

        <Input
          label="Entity type"
          placeholder="agencies, orders …"
          defaultValue={entityType}
          onChange={(event) => change('entityType', event.target.value)}
        />

        <Input
          label="From"
          type="date"
          value={from}
          onChange={(event) => change('from', event.target.value)}
        />

        <Input
          label="To"
          type="date"
          value={to}
          hint="Inclusive — the whole of that day."
          onChange={(event) => change('to', event.target.value)}
        />
      </div>

      {agencyId ? (
        <div className="flex flex-wrap items-center gap-2 text-sm text-muted-foreground">
          <span>Showing one agency only.</span>
          <Button variant="outline" size="sm" onClick={() => change('agencyId', '')}>
            Show every agency
          </Button>
        </div>
      ) : null}

      {trail.isError ? (
        <ErrorState
          {...describeLoadError(trail.error)}
          onRetry={() => void trail.refetch()}
          retrying={trail.isFetching}
        />
      ) : null}

      <Card className="overflow-hidden">
        {trail.isPending ? <AuditTableSkeleton /> : null}

        {trail.data && trail.data.items.length === 0 ? (
          <EmptyState
            title="Nothing matches this"
            action={
              filtered ? (
                <Button
                  variant="outline"
                  size="sm"
                  onClick={() => setParams({}, { replace: true })}
                >
                  Clear the filters
                </Button>
              ) : undefined
            }
          >
            {filtered
              ? 'Try a wider date range, or a different action.'
              : 'Nothing has been recorded yet. The first action anybody takes will appear here.'}
          </EmptyState>
        ) : null}

        {trail.data && trail.data.items.length > 0 ? (
          <AuditTable
            entries={trail.data.items}
            selectedId={selected?.id ?? null}
            onSelect={setSelected}
          />
        ) : null}
      </Card>

      {total > PAGE_SIZE ? (
        <div className="flex flex-wrap items-center justify-between gap-3">
          <p className="text-sm text-muted-foreground" aria-live="polite">
            Page {page} of {lastPage} · {total} entries
          </p>
          <div className="flex gap-2">
            <Button
              variant="outline"
              size="sm"
              disabled={page <= 1}
              onClick={() => change('page', String(page - 1), false)}
            >
              Previous
            </Button>
            <Button
              variant="outline"
              size="sm"
              disabled={page >= lastPage}
              onClick={() => change('page', String(page + 1), false)}
            >
              Next
            </Button>
          </div>
        </div>
      ) : null}

      <EntryDetail entry={selected} onClose={() => setSelected(null)} />
    </Page>
  );
}

/**
 * A date from the picker as an instant the API can filter on.
 *
 * The picker gives a bare `2026-09-12` with no time and no zone. Parsed as local midnight rather
 * than as UTC, because somebody in Lagos asking for "the 12th" means their 12th; `toISOString`
 * then hands the API UTC, which is the only thing Npgsql will store. `to` is pushed to the end of
 * the chosen day, so a single-day range is the day rather than one empty instant of it.
 */
function toInstant(date: string, endOfDay = false): string | undefined {
  if (!date) return undefined;

  const parsed = new Date(`${date}T${endOfDay ? '23:59:59.999' : '00:00:00.000'}`);
  return Number.isNaN(parsed.getTime()) ? undefined : parsed.toISOString();
}
