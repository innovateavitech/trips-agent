import { useSearchParams } from 'react-router-dom';
import { Button, Card, Input, Select } from '@trips/ui';
import { Page, PageHeader } from '../../../components/page';
import { RefreshIcon } from '../../../components/icons';
import { EmptyState, ErrorState } from '../../../components/states';
import { describeLoadError } from '../../../lib/api/problem';
import { useDocumentTitle } from '../../../lib/hooks';
import { DirectoryTable, DirectoryTableSkeleton } from '../components/directory-table';
import { useAgencyDirectory } from '../agency-queries';
import type { AgencySort, AgencyStatus, AgencyType } from '../types';

/** The statuses a filter may ask for, in lifecycle order rather than alphabetical. */
const STATUSES: { value: AgencyStatus; label: string }[] = [
  { value: 'PendingVerification', label: 'Awaiting verification' },
  { value: 'Verified', label: 'Verified' },
  { value: 'Rejected', label: 'Verification refused' },
  { value: 'Suspended', label: 'Suspended' },
  { value: 'Terminated', label: 'Terminated' },
];

const SORTS: { value: AgencySort; label: string }[] = [
  { value: 'Newest', label: 'Newest first' },
  { value: 'Oldest', label: 'Oldest first' },
  { value: 'Name', label: 'By name' },
];

const PAGE_SIZE = 25;

/**
 * The agency directory: every travel business on the platform, searchable and filtered.
 *
 * Every control writes to the address bar rather than to component state, so a filtered view can
 * be reloaded, bookmarked, or sent to a colleague — which is what actually happens when somebody
 * asks "which ones are suspended?".
 */
export function AgencyDirectoryPage() {
  useDocumentTitle('Agencies');

  const [params, setParams] = useSearchParams();

  const search = params.get('search') ?? '';
  const status = readEnum(
    params.get('status'),
    STATUSES.map((option) => option.value),
  );
  const type = readEnum(params.get('type'), ['Principal', 'SubAgent'] as AgencyType[]);
  const sort =
    readEnum(
      params.get('sort'),
      SORTS.map((option) => option.value),
    ) ?? 'Newest';
  const page = Math.max(1, Number(params.get('page') ?? '1') || 1);

  const directory = useAgencyDirectory({
    search,
    status,
    type,
    sort,
    page,
    pageSize: PAGE_SIZE,
  });

  /** Writes one control back to the URL, and returns to page one whenever the filter changes. */
  function change(key: string, value: string, resetPage = true) {
    const next = new URLSearchParams(params);

    if (value === '') next.delete(key);
    else next.set(key, value);

    if (resetPage) next.delete('page');

    setParams(next, { replace: true });
  }

  const total = directory.data?.totalCount ?? 0;
  const lastPage = Math.max(1, Math.ceil(total / PAGE_SIZE));

  return (
    <Page wide>
      <PageHeader
        title="Agencies"
        description="Every travel business on the platform, and where each of them stands."
        actions={
          <Button
            variant="outline"
            size="sm"
            onClick={() => void directory.refetch()}
            loading={directory.isFetching}
          >
            {directory.isFetching ? null : <RefreshIcon />}
            Refresh
          </Button>
        }
      />

      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <Input
          label="Search"
          type="search"
          placeholder="Name or slug"
          defaultValue={search}
          hint="Matches the legal name, the trading name and the slug."
          onChange={(event) => change('search', event.target.value)}
        />

        <Select
          label="Status"
          value={status ?? ''}
          onChange={(event) => change('status', event.target.value)}
        >
          <option value="">Any status</option>
          {STATUSES.map((option) => (
            <option key={option.value} value={option.value}>
              {option.label}
            </option>
          ))}
        </Select>

        <Select
          label="Type"
          value={type ?? ''}
          onChange={(event) => change('type', event.target.value)}
        >
          <option value="">Principals and sub-agents</option>
          <option value="Principal">Principals</option>
          <option value="SubAgent">Sub-agents</option>
        </Select>

        <Select label="Sort" value={sort} onChange={(event) => change('sort', event.target.value)}>
          {SORTS.map((option) => (
            <option key={option.value} value={option.value}>
              {option.label}
            </option>
          ))}
        </Select>
      </div>

      {directory.isError ? (
        <ErrorState
          {...describeLoadError(directory.error)}
          onRetry={() => void directory.refetch()}
          retrying={directory.isFetching}
        />
      ) : null}

      <Card className="overflow-hidden">
        {directory.isPending ? <DirectoryTableSkeleton /> : null}

        {directory.data && directory.data.items.length === 0 ? (
          <EmptyState
            title="No agency matches this"
            action={
              <Button variant="outline" size="sm" onClick={() => setParams({}, { replace: true })}>
                Clear the filters
              </Button>
            }
          >
            Try a shorter search term, or widen the status filter.
          </EmptyState>
        ) : null}

        {directory.data && directory.data.items.length > 0 ? (
          <DirectoryTable agencies={directory.data.items} />
        ) : null}
      </Card>

      {total > PAGE_SIZE ? (
        <div className="flex flex-wrap items-center justify-between gap-3">
          <p className="text-sm text-muted-foreground" aria-live="polite">
            Page {page} of {lastPage} · {total} agencies
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
    </Page>
  );
}

/**
 * Reads one value out of the address bar, or nothing if it is not one we recognise.
 *
 * A hand-edited URL should fall back to "no filter" rather than be sent to the API, which would
 * answer 400 and leave somebody staring at an error they cannot act on.
 */
function readEnum<T extends string>(value: string | null, allowed: T[]): T | undefined {
  return allowed.find((option) => option === value);
}
