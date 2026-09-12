import { useEffect, useState, type FormEvent } from 'react';
import {
  Alert,
  Badge,
  Button,
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogTitle,
  Input,
  Select,
  Textarea,
} from '@trips/ui';
import { describeLoadError, fieldError } from '../../../lib/api/problem';
import { MAX_REASON_LENGTH, validateReason } from '../../../lib/reason';
import { isReadOnly, summarise } from '../back-office-rules';
import type { CreatePlatformUserRequest, PlatformRole } from '../types';

/**
 * Creating a back-office account.
 *
 * No password field, and that is the point: the account is created invited and the person sets
 * their own through the ordinary reset flow. A password chosen by an administrator is a password
 * an administrator knows.
 *
 * The chosen role's abilities are listed under the picker, in the words the API sends, so the
 * decision is made with what the role actually carries in view rather than from the name alone.
 */
export function InviteDialog({
  open,
  onOpenChange,
  roles,
  onSubmit,
  pending,
  error,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  roles: PlatformRole[];
  onSubmit: (request: CreatePlatformUserRequest) => void;
  pending: boolean;
  error: unknown;
}) {
  const [email, setEmail] = useState('');
  const [firstName, setFirstName] = useState('');
  const [lastName, setLastName] = useState('');
  const [roleName, setRoleName] = useState('');
  const [reason, setReason] = useState('');
  const [reasonProblem, setReasonProblem] = useState<string>();

  // Cleared as it closes rather than as it opens, so reopening to finish a half-typed sentence
  // does not wipe it.
  useEffect(() => {
    if (!open) {
      setEmail('');
      setFirstName('');
      setLastName('');
      setRoleName('');
      setReason('');
      setReasonProblem(undefined);
    }
  }, [open]);

  const chosen = roles.find((role) => role.name === roleName);

  function submit(event: FormEvent) {
    event.preventDefault();

    const invalid = validateReason(reason);
    setReasonProblem(invalid);

    if (invalid) return;

    onSubmit({
      email: email.trim(),
      firstName: firstName.trim(),
      lastName: lastName.trim(),
      roleName,
      reason: reason.trim(),
    });
  }

  const failure = error ? describeLoadError(error) : null;

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent aria-describedby={undefined}>
        <DialogTitle>Create a back-office account</DialogTitle>
        <DialogDescription>
          They are invited, not given a password. They set their own before they can sign in.
        </DialogDescription>

        {failure ? (
          <Alert tone="destructive" title={failure.title}>
            {failure.detail}
          </Alert>
        ) : null}

        <form className="flex flex-col gap-4" onSubmit={submit}>
          <div className="grid gap-3 sm:grid-cols-2">
            <Input
              label="First name"
              required
              value={firstName}
              error={fieldError(error, 'firstName')}
              onChange={(event) => setFirstName(event.target.value)}
            />
            <Input
              label="Last name"
              required
              value={lastName}
              error={fieldError(error, 'lastName')}
              onChange={(event) => setLastName(event.target.value)}
            />
          </div>

          <Input
            label="Email"
            type="email"
            required
            autoComplete="off"
            value={email}
            error={fieldError(error, 'email')}
            hint="One address is one account, agency or back-office."
            onChange={(event) => setEmail(event.target.value)}
          />

          <Select
            label="Role"
            required
            value={roleName}
            error={fieldError(error, 'roleName')}
            onChange={(event) => setRoleName(event.target.value)}
          >
            <option value="">Choose a role</option>
            {roles.map((role) => (
              <option key={role.id} value={role.name}>
                {role.name}
              </option>
            ))}
          </Select>

          {chosen ? <RoleSummaryPanel role={chosen} /> : null}

          <Textarea
            label="Why"
            rows={3}
            required
            maxLength={MAX_REASON_LENGTH}
            value={reason}
            error={reasonProblem}
            hint="Who asked for this account, and what it is for."
            onChange={(event) => setReason(event.target.value)}
          />

          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
              Cancel
            </Button>
            <Button type="submit" loading={pending} disabled={roleName === ''}>
              Create the account
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

/** What the chosen role carries, from the API's own answer rather than a list kept here. */
export function RoleSummaryPanel({ role }: { role: PlatformRole }) {
  const summary = summarise(role);

  return (
    <div className="flex flex-col gap-2 rounded-md bg-muted p-3">
      <div className="flex flex-wrap items-center gap-2">
        <p className="text-sm font-medium text-foreground">{role.name}</p>
        {isReadOnly(role) ? <Badge tone="neutral">Cannot change an agency</Badge> : null}
      </div>

      <p className="text-xs text-muted-foreground">{role.description}</p>

      {summary.can.length > 0 ? (
        <ul className="flex list-disc flex-col gap-1 pl-5 text-xs text-muted-foreground">
          {summary.can.map((ability) => (
            <li key={ability}>They can {ability}.</li>
          ))}
        </ul>
      ) : null}

      {summary.unnamedCount > 0 ? (
        <p className="text-xs text-muted-foreground">
          And {summary.unnamedCount} further permission{summary.unnamedCount === 1 ? '' : 's'} this
          screen has no wording for.
        </p>
      ) : null}
    </div>
  );
}
