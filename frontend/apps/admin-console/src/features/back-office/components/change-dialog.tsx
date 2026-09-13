import { useEffect, useState, type FormEvent } from 'react';
import {
  Alert,
  Button,
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogTitle,
  Select,
  Textarea,
} from '@trips/ui';
import { describeLoadError } from '../../../lib/api/problem';
import { MAX_REASON_LENGTH, validateReason } from '../../../lib/reason';
import { displayName, userStatusDisplay } from '../back-office-rules';
import { RoleSummaryPanel } from './invite-dialog';
import type { PlatformRole, PlatformUser, PlatformUserStatus } from '../types';

/**
 * The two changes made to an existing account: the role it holds, and whether it can sign in.
 *
 * One dialog for both because they are the same shape — pick the new value, say why, confirm —
 * and because the reason is the part that matters and it should look identical either way.
 *
 * Mount it with a `key` that changes per account and per kind, so a half-typed reason belongs to
 * one decision and is never still in the box when a different one is opened.
 */
export function ChangeDialog({
  user,
  kind,
  roles,
  toStatus,
  onClose,
  onConfirm,
  pending,
  error,
}: {
  user: PlatformUser;
  kind: 'role' | 'status';
  roles: PlatformRole[];
  /** Where the status is going. Only read when `kind` is `status`. */
  toStatus: PlatformUserStatus;
  onClose: () => void;
  onConfirm: (value: string, reason: string) => void;
  pending: boolean;
  error: unknown;
}) {
  const [roleName, setRoleName] = useState(user.roles[0] ?? '');
  const [reason, setReason] = useState('');
  const [problem, setProblem] = useState<string>();

  useEffect(() => {
    setRoleName(user.roles[0] ?? '');
  }, [user]);

  function submit(event: FormEvent) {
    event.preventDefault();

    const invalid = validateReason(reason);
    setProblem(invalid);

    if (!invalid) onConfirm(kind === 'role' ? roleName : toStatus, reason.trim());
  }

  const failure = error ? describeLoadError(error) : null;
  const chosen = roles.find((role) => role.name === roleName);
  const name = displayName(user);
  const destination = userStatusDisplay(toStatus);
  const suspending = kind === 'status' && toStatus === 'Suspended';

  return (
    <Dialog open onOpenChange={(open) => (open ? undefined : onClose())}>
      <DialogContent aria-describedby={undefined}>
        <DialogTitle>
          {kind === 'role' ? `Change ${name}’s role` : `${destination.label}: ${name}`}
        </DialogTitle>

        <DialogDescription>
          {kind === 'role'
            ? 'The new role replaces what they hold now rather than being added to it.'
            : destination.meaning}{' '}
          This is recorded in the audit log against your name.
        </DialogDescription>

        {suspending ? (
          <p className="text-sm text-muted-foreground">
            Their account stops working immediately. Everything they have already done stays in the
            audit log under their name, as it must.
          </p>
        ) : null}

        {failure ? (
          <Alert tone="destructive" title={failure.title}>
            {failure.detail}
          </Alert>
        ) : null}

        <form className="flex flex-col gap-4" onSubmit={submit}>
          {kind === 'role' ? (
            <>
              <Select
                label="New role"
                required
                value={roleName}
                onChange={(event) => setRoleName(event.target.value)}
              >
                {roles.map((role) => (
                  <option key={role.id} value={role.name}>
                    {role.name}
                  </option>
                ))}
              </Select>

              {chosen ? <RoleSummaryPanel role={chosen} /> : null}
            </>
          ) : null}

          <Textarea
            label="Why"
            rows={3}
            required
            maxLength={MAX_REASON_LENGTH}
            value={reason}
            error={problem}
            hint="Written for whoever reads this in a year, not for the person next to you."
            onChange={(event) => setReason(event.target.value)}
          />

          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose}>
              Cancel
            </Button>
            <Button
              type="submit"
              variant={suspending ? 'destructive' : 'primary'}
              loading={pending}
              disabled={kind === 'role' && roleName === ''}
            >
              {kind === 'role' ? 'Change the role' : destination.label}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
