import {
  Badge,
  Button,
  Skeleton,
  Table,
  TableBody,
  TableCaption,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@trips/ui';
import { formatDateTime } from '../../../lib/format';
import { displayName, isSelf, nextStatus, userStatusDisplay } from '../back-office-rules';
import type { PlatformUser, PlatformUserStatus } from '../types';

/**
 * Trips' own accounts: who holds which role, and whether they can sign in.
 *
 * The role is a column rather than a detail, because it is the whole point of the screen — a
 * Super Admin opens this to answer "who can suspend a customer", and that is a question the list
 * itself should answer without a click.
 */
export function UserTable({
  users,
  signedInUserId,
  onChangeRole,
  onChangeStatus,
}: {
  users: PlatformUser[];
  signedInUserId: string | null;
  onChangeRole: (user: PlatformUser) => void;
  onChangeStatus: (user: PlatformUser, to: PlatformUserStatus) => void;
}) {
  return (
    <Table>
      <TableCaption className="pb-3">
        Every Trips back-office account. Agency staff are not listed here — they belong to their own
        agency.
      </TableCaption>

      <TableHeader>
        <TableRow className="hover:bg-transparent">
          <TableHead scope="col">Person</TableHead>
          <TableHead scope="col">Role</TableHead>
          <TableHead scope="col">Status</TableHead>
          <TableHead scope="col">Last signed in</TableHead>
          <TableHead scope="col">
            <span className="sr-only">Actions</span>
          </TableHead>
        </TableRow>
      </TableHeader>

      <TableBody>
        {users.map((user) => {
          const status = userStatusDisplay(user.status);
          const move = nextStatus(user.status);
          const self = isSelf(user, signedInUserId);
          const name = displayName(user);

          return (
            <TableRow key={user.id}>
              <TableCell className="min-w-56">
                <p className="font-medium text-foreground">
                  {name}
                  {self ? (
                    <span className="ml-2 text-xs font-normal text-muted-foreground">(you)</span>
                  ) : null}
                </p>
                <p className="text-xs text-muted-foreground">{user.email}</p>
              </TableCell>

              <TableCell>
                {user.roles.length === 0 ? (
                  // Should not happen — an account is created with one. If it does, it is worth
                  // seeing rather than rendering as a blank cell somebody reads straight past.
                  <Badge tone="destructive">No role</Badge>
                ) : (
                  <div className="flex flex-wrap gap-1">
                    {user.roles.map((role) => (
                      <Badge key={role} tone="info">
                        {role}
                      </Badge>
                    ))}
                  </div>
                )}
              </TableCell>

              <TableCell>
                <Badge tone={status.tone}>{status.label}</Badge>
                <p className="mt-1 max-w-64 text-xs text-muted-foreground">{status.meaning}</p>
              </TableCell>

              <TableCell className="whitespace-nowrap tabular-nums text-muted-foreground">
                {user.lastLoginAt ? formatDateTime(user.lastLoginAt) : 'Never'}
              </TableCell>

              <TableCell>
                <div className="flex justify-end gap-2">
                  <Button variant="outline" size="sm" onClick={() => onChangeRole(user)}>
                    Change role<span className="sr-only"> for {name}</span>
                  </Button>

                  {move && !self ? (
                    <Button
                      variant={move.destructive ? 'destructive' : 'outline'}
                      size="sm"
                      onClick={() => onChangeStatus(user, move.to)}
                    >
                      {move.label}
                      <span className="sr-only"> {name}</span>
                    </Button>
                  ) : null}
                </div>
              </TableCell>
            </TableRow>
          );
        })}
      </TableBody>
    </Table>
  );
}

export function UserTableSkeleton({ rows = 5 }: { rows?: number }) {
  return (
    <div aria-busy="true" aria-label="Loading the accounts" className="flex flex-col gap-3 p-4">
      <Skeleton className="h-4 w-1/3" />
      {Array.from({ length: rows }, (_, index) => (
        <Skeleton key={index} className="h-10 w-full" />
      ))}
    </div>
  );
}
