import { Alert, Button } from '@trips/ui';
import { Page, PageHeader } from '../../../components/page';
import { RefreshIcon } from '../../../components/icons';
import { ErrorState } from '../../../components/states';
import { describeLoadError } from '../../../lib/api/problem';
import { useDocumentTitle, useNow } from '../../../lib/hooks';
import { AlertsList } from '../components/alerts-list';
import { DashboardSkeleton, SalesTiles, StatTiles } from '../components/stat-tiles';
import { freshness, needsSomebodyNow } from '../dashboard-rules';
import { useOperationsDashboard } from '../dashboard-queries';

/**
 * The back office's front page: what is waiting, what sold, and what is on fire.
 *
 * Every figure says how old it is. A dashboard that does not is a dashboard somebody quotes in a
 * meeting three hours after it stopped being true.
 */
export function DashboardPage() {
  useDocumentTitle('Dashboard');

  const dashboard = useOperationsDashboard();
  const now = useNow(30_000);

  if (dashboard.isPending) {
    return (
      <Page wide>
        <PageHeader title="Dashboard" description="Counting across every agency." />
        <DashboardSkeleton />
      </Page>
    );
  }

  if (dashboard.isError) {
    return (
      <Page wide>
        <PageHeader title="Dashboard" />
        <ErrorState
          {...describeLoadError(dashboard.error)}
          onRetry={() => void dashboard.refetch()}
          retrying={dashboard.isFetching}
        />
      </Page>
    );
  }

  const data = dashboard.data;
  const age = freshness(data, now);

  return (
    <Page wide>
      <PageHeader
        title="Dashboard"
        description="Everything across every agency, counted on the server so the numbers agree with each other."
        actions={
          <Button
            variant="outline"
            size="sm"
            onClick={() => void dashboard.refetch()}
            loading={dashboard.isFetching}
          >
            {dashboard.isFetching ? null : <RefreshIcon />}
            Refresh
          </Button>
        }
      >
        <p className="text-xs text-muted-foreground" aria-live="polite">
          {age.label}
          {age.stale ? ' — older than it should be. Refresh before acting on it.' : '.'}
        </p>
      </PageHeader>

      {needsSomebodyNow(data) ? (
        <Alert tone="destructive" title="Something needs a person now">
          {data.bookingsNeedingResolution > 0
            ? `${data.bookingsNeedingResolution} booking${data.bookingsNeedingResolution === 1 ? ' has' : 's have'} been paid for and not delivered. `
            : ''}
          {data.criticalAlertCount > 0
            ? `${data.criticalAlertCount} critical alert${data.criticalAlertCount === 1 ? ' is' : 's are'} open.`
            : ''}
        </Alert>
      ) : null}

      <StatTiles dashboard={data} />

      <section className="flex flex-col gap-3">
        <h2 className="text-sm font-semibold text-foreground">Sales</h2>
        <SalesTiles sales={data.sales} />
        <p className="text-xs text-muted-foreground">
          Gross is what travellers paid: net plus the agency&rsquo;s markup plus tax. The platform
          fee comes out of the agency&rsquo;s margin and is not added to it.
        </p>
      </section>

      <AlertsList alerts={data.alerts} />
    </Page>
  );
}
