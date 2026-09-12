import { useState } from 'react';
import { Button, Card } from '@trips/ui';
import { Page, PageHeader } from '../../../components/page';
import { EmptyState, ErrorState } from '../../../components/states';
import { describeLoadError } from '../../../lib/api/problem';
import { useDocumentTitle } from '../../../lib/hooks';
import { useAuth } from '../../auth/auth-context';
import { ChangeDialog } from '../components/change-dialog';
import { InviteDialog } from '../components/invite-dialog';
import { UserTable, UserTableSkeleton } from '../components/user-table';
import {
  useBackOfficeUsers,
  useChangeRole,
  useChangeStatus,
  useCreateBackOfficeUser,
  usePlatformRoles,
} from '../back-office-queries';
import type { PlatformUser, PlatformUserStatus } from '../types';

/** Which account is being changed, and which of the two changes it is. */
interface Pending {
  user: PlatformUser;
  kind: 'role' | 'status';
  toStatus: PlatformUserStatus;
}

/**
 * Trips' own accounts.
 *
 * The screen behind `platform.user.manage`, which only a Super Admin holds — this is where
 * somebody is granted the ability to suspend a customer, so the list of people who can open it is
 * deliberately the shortest one in the console.
 */
export function BackOfficeUsersPage() {
  useDocumentTitle('Back-office users');

  const { session } = useAuth();
  const users = useBackOfficeUsers();
  const roles = usePlatformRoles();

  const [inviting, setInviting] = useState(false);
  const [pending, setPending] = useState<Pending | null>(null);

  const create = useCreateBackOfficeUser();
  const changeRole = useChangeRole(pending?.user.id ?? '');
  const changeStatus = useChangeStatus(pending?.user.id ?? '');

  const roleList = roles.data ?? [];
  const active = pending?.kind === 'role' ? changeRole : changeStatus;

  function close() {
    setPending(null);
    changeRole.reset();
    changeStatus.reset();
  }

  return (
    <Page wide>
      <PageHeader
        title="Back-office users"
        description="Trips staff, and what each of them is allowed to do."
        actions={
          <Button
            size="sm"
            onClick={() => {
              create.reset();
              setInviting(true);
            }}
            disabled={roleList.length === 0}
          >
            Create an account
          </Button>
        }
      />

      {users.isError ? (
        <ErrorState
          {...describeLoadError(users.error)}
          onRetry={() => void users.refetch()}
          retrying={users.isFetching}
        />
      ) : null}

      <Card className="overflow-hidden">
        {users.isPending ? <UserTableSkeleton /> : null}

        {users.data && users.data.length === 0 ? (
          <EmptyState title="There are no back-office accounts">
            That should not be possible — you are signed in to one. Refresh, and tell an engineer if
            it stays empty.
          </EmptyState>
        ) : null}

        {users.data && users.data.length > 0 ? (
          <UserTable
            users={users.data}
            signedInUserId={session?.claims.userId ?? null}
            onChangeRole={(user) => {
              changeRole.reset();
              setPending({ user, kind: 'role', toStatus: user.status });
            }}
            onChangeStatus={(user, toStatus) => {
              changeStatus.reset();
              setPending({ user, kind: 'status', toStatus });
            }}
          />
        ) : null}
      </Card>

      <InviteDialog
        open={inviting}
        onOpenChange={setInviting}
        roles={roleList}
        pending={create.isPending}
        error={create.error}
        onSubmit={(request) => create.mutate(request, { onSuccess: () => setInviting(false) })}
      />

      {pending ? (
        <ChangeDialog
          // Keyed per account and per kind, so a half-typed reason never survives into a
          // different decision.
          key={`${pending.user.id}-${pending.kind}`}
          user={pending.user}
          kind={pending.kind}
          roles={roleList}
          toStatus={pending.toStatus}
          pending={active.isPending}
          error={active.error}
          onClose={close}
          onConfirm={(value, reason) => {
            if (pending.kind === 'role') {
              changeRole.mutate({ roleName: value, reason }, { onSuccess: close });
            } else {
              changeStatus.mutate(
                { status: value as PlatformUserStatus, reason },
                { onSuccess: close },
              );
            }
          }}
        />
      ) : null}
    </Page>
  );
}
