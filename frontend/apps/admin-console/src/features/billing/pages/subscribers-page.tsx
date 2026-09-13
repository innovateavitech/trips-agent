import { useSearchParams } from 'react-router-dom';
import { Card, Select } from '@trips/ui';
import { Page, PageHeader } from '../../../components/page';
import { EmptyState, ErrorState } from '../../../components/states';
import { describeLoadError } from '../../../lib/api/problem';
import { formatMoney } from '../../../lib/format';
import { useDocumentTitle } from '../../../lib/hooks';
import { SubscriberTable, SubscriberTableSkeleton } from '../components/subscriber-table';
import { useSubscribers, useTiers } from '../billing-queries';

/**
 * Who is on which plan, and who owes us money.
 *
 * The filter lives in the URL so a link to "everyone on Growth" can be pasted into a conversation
 * — which is how this screen is actually used when somebody asks about a plan.
 */
export function SubscribersPage() {
  useDocumentTitle('Subscribers');

  const [params, setParams] = useSearchParams();
  const tierId = params.get('tier') ?? '';

  const tiers = useTiers(true);
  const subscribers = useSubscribers(tierId === '' ? undefined : tierId);

  const list = subscribers.data ?? [];
  const owing = list.reduce((total, subscriber) => total + subscriber.outstandingMinor, 0);
  const pastDue = list.filter((subscriber) => subscriber.status === 'PastDue').length;

  return (
    <Page wide>
      <PageHeader
        title="Subscribers"
        description="Every agency with a live subscription, and what it owes Trips."
      />

      {subscribers.isError ? (
        <ErrorState
          {...describeLoadError(subscribers.error)}
          onRetry={() => void subscribers.refetch()}
          retrying={subscribers.isFetching}
        />
      ) : null}

      <div className="flex flex-wrap items-end gap-4">
        <div className="w-full max-w-xs">
          <Select
            label="Plan"
            value={tierId}
            onChange={(event) => {
              const next = new URLSearchParams(params);

              if (event.target.value === '') next.delete('tier');
              else next.set('tier', event.target.value);

              setParams(next, { replace: true });
            }}
          >
            <option value="">Every plan</option>
            {(tiers.data ?? []).map((tier) => (
              <option key={tier.id} value={tier.id}>
                {tier.name}
              </option>
            ))}
          </Select>
        </div>

        <dl className="flex gap-6">
          <Total label="Subscribers" value={String(list.length)} />
          <Total label="Past due" value={String(pastDue)} />
          <Total label="Outstanding" value={formatMoney(owing)} />
        </dl>
      </div>

      <Card className="overflow-hidden">
        {subscribers.isPending ? <SubscriberTableSkeleton /> : null}

        {subscribers.data && list.length === 0 ? (
          <EmptyState title="Nobody is on this plan">
            Agencies appear here as soon as they subscribe. A plan with no subscribers is a plan
            that can still be deleted.
          </EmptyState>
        ) : null}

        {list.length > 0 ? <SubscriberTable subscribers={list} /> : null}
      </Card>
    </Page>
  );
}

function Total({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex flex-col">
      <dt className="text-xs text-muted-foreground">{label}</dt>
      <dd className="text-lg font-semibold text-foreground">{value}</dd>
    </div>
  );
}
