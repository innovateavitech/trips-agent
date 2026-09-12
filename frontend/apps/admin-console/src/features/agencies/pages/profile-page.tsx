import { useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { Alert, Badge, Button, Card, Skeleton, buttonVariants } from '@trips/ui';
import { BackLink, Page, PageHeader } from '../../../components/page';
import { EmptyState, ErrorState } from '../../../components/states';
import { describeLoadError } from '../../../lib/api/problem';
import { useDocumentTitle } from '../../../lib/hooks';
import { useAuth } from '../../auth/auth-context';
import { AgencyFacts, AgencyStaff } from '../components/agency-facts';
import { EditAgencyForm } from '../components/edit-agency-form';
import { LifecyclePanel } from '../components/lifecycle-panel';
import { agencyStatusDisplay, canEdit, profileWarning } from '../agency-rules';
import { useAgencyProfile } from '../agency-queries';

/**
 * One agency in full: who they are, what they hold, who works there, and what can be done to them.
 *
 * The warning banner is the first thing on the page when an agency cannot trade, because it is the
 * fact that changes how every other number on the screen should be read.
 */
export function AgencyProfilePage() {
  const { agencyId = '' } = useParams();
  const profile = useAgencyProfile(agencyId);
  const { session } = useAuth();
  const [editing, setEditing] = useState(false);

  useDocumentTitle(profile.data?.name ?? 'Agency');

  if (profile.isPending) return <ProfileSkeleton />;

  if (profile.isError) {
    return (
      <Page>
        <BackLink to="/agencies">All agencies</BackLink>
        <ErrorState
          {...describeLoadError(profile.error)}
          onRetry={() => void profile.refetch()}
          retrying={profile.isFetching}
        />
      </Page>
    );
  }

  if (!profile.data) {
    return (
      <Page>
        <Card>
          <EmptyState
            title="There is no agency at this address"
            action={
              <Link to="/agencies" className={buttonVariants({ variant: 'outline' })}>
                Back to the directory
              </Link>
            }
          >
            The link may be out of date.
          </EmptyState>
        </Card>
      </Page>
    );
  }

  const agency = profile.data;
  const status = agencyStatusDisplay(agency.status);
  const warning = profileWarning(agency);

  return (
    <Page wide>
      <BackLink to="/agencies">All agencies</BackLink>

      <PageHeader
        title={agency.name}
        description={status.meaning}
        actions={
          canEdit(session?.claims.permissions ?? []) && !editing ? (
            <Button variant="outline" onClick={() => setEditing(true)}>
              Edit details
            </Button>
          ) : null
        }
      >
        <div className="flex flex-wrap items-center gap-2">
          <Badge tone={status.tone}>{status.label}</Badge>
          {agency.canTakeNewBookings ? null : <Badge tone="warning">No new bookings</Badge>}
          {agency.storefrontIsLive ? null : <Badge tone="warning">Storefront offline</Badge>}
        </div>
      </PageHeader>

      {warning ? (
        <Alert tone="warning" title={warning.title}>
          {warning.detail}
        </Alert>
      ) : null}

      {editing ? (
        <EditAgencyForm profile={agency} onDone={() => setEditing(false)} />
      ) : (
        <AgencyFacts profile={agency} />
      )}

      <LifecyclePanel profile={agency} />

      <AgencyStaff profile={agency} />

      {agency.subAgents.length > 0 ? (
        <Card className="p-4">
          <h2 className="mb-3 text-sm font-semibold text-foreground">
            Sub-agents ({agency.subAgents.length})
          </h2>
          <ul className="flex flex-col divide-y divide-border">
            {agency.subAgents.map((subAgent) => (
              <li key={subAgent.id} className="flex items-center justify-between gap-3 py-2.5">
                <Link
                  to={`/agencies/${subAgent.id}`}
                  className="text-sm font-medium text-foreground underline-offset-4 hover:underline"
                >
                  {subAgent.name}
                </Link>
                <Badge tone={agencyStatusDisplay(subAgent.status).tone}>
                  {agencyStatusDisplay(subAgent.status).label}
                </Badge>
              </li>
            ))}
          </ul>
        </Card>
      ) : null}
    </Page>
  );
}

/** The same shape as the loaded page, so nothing jumps when it arrives. */
function ProfileSkeleton() {
  return (
    <Page wide>
      <div aria-busy="true" aria-label="Loading the agency" className="flex flex-col gap-4">
        <Skeleton className="h-8 w-64" />
        <Skeleton className="h-4 w-96" />
        <div className="grid gap-4 lg:grid-cols-2">
          <Skeleton className="h-56 w-full" />
          <Skeleton className="h-56 w-full" />
        </div>
        <Skeleton className="h-40 w-full" />
      </div>
    </Page>
  );
}
