import { useState } from 'react';
import {
  Alert,
  Button,
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogTitle,
  DialogTrigger,
  Input,
} from '@trips/ui';
import { describeError, fieldError } from '../../../api/errors';
import { useInviteSubAgent } from '../subagents-api';
import type { SubAgentInvited } from '../types';

/**
 * Creates an agency beneath this one and invites the person who will run it.
 *
 * The invitation email carries the principal's own name and branding, never
 * Trips' — build-plan decision 6 — so the copy here says "your brand" and means
 * it. The one-time link is shown afterwards because email is not reliable and
 * the alternative is a support ticket; it is never shown twice.
 */
export function InviteSubAgentDialog({
  canAddAnother,
  cannotAddReason,
}: {
  canAddAnother: boolean;
  cannotAddReason: string | null;
}) {
  const [open, setOpen] = useState(false);
  const [legalName, setLegalName] = useState('');
  const [tradingName, setTradingName] = useState('');
  const [email, setEmail] = useState('');
  const [invited, setInvited] = useState<SubAgentInvited | null>(null);

  const invite = useInviteSubAgent();

  function close(next: boolean) {
    setOpen(next);

    if (!next) {
      setLegalName('');
      setTradingName('');
      setEmail('');
      setInvited(null);
      invite.reset();
    }
  }

  return (
    <Dialog open={open} onOpenChange={close}>
      <DialogTrigger asChild>
        <Button disabled={!canAddAnother} title={cannotAddReason ?? undefined}>
          Invite a sub-agent
        </Button>
      </DialogTrigger>

      <DialogContent>
        {invited ? (
          <Invited invited={invited} onDone={() => close(false)} />
        ) : (
          <form
            className="flex flex-col gap-4"
            onSubmit={(event) => {
              event.preventDefault();

              invite.mutate(
                {
                  legalName: legalName.trim(),
                  tradingName: tradingName.trim() === '' ? null : tradingName.trim(),
                  email: email.trim(),
                },
                { onSuccess: setInvited },
              );
            }}
          >
            <DialogTitle>Invite a sub-agent</DialogTitle>
            <DialogDescription>
              They get their own console and sell under your brand. Nothing they see mentions anyone
              but you.
            </DialogDescription>

            {invite.isError ? <InviteError error={invite.error} /> : null}

            <Input
              label="Registered business name"
              value={legalName}
              onChange={(event) => setLegalName(event.target.value)}
              placeholder="Ikeja Branch Limited"
              required
              autoFocus
            />

            <Input
              label="Trading name"
              hint="Optional — what they actually trade as, if it differs."
              value={tradingName}
              onChange={(event) => setTradingName(event.target.value)}
              placeholder="Ikeja Travel"
            />

            <Input
              label="Email of the person who will run it"
              type="email"
              value={email}
              onChange={(event) => setEmail(event.target.value)}
              placeholder="owner@ikejatravel.com"
              required
            />

            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => close(false)}>
                Cancel
              </Button>
              <Button type="submit" disabled={invite.isPending}>
                {invite.isPending ? 'Sending…' : 'Send the invitation'}
              </Button>
            </DialogFooter>
          </form>
        )}
      </DialogContent>
    </Dialog>
  );
}

function InviteError({ error }: { error: unknown }) {
  const problem = describeError(error);
  const forName = fieldError(error, 'legalName');
  const forEmail = fieldError(error, 'email');
  const forRequest = fieldError(error, 'request');

  return (
    <Alert tone="destructive" title={problem.title}>
      {forName ?? forEmail ?? forRequest ?? problem.detail}
    </Alert>
  );
}

function Invited({ invited, onDone }: { invited: SubAgentInvited; onDone: () => void }) {
  return (
    <div className="flex flex-col gap-4">
      <DialogTitle>Invitation sent</DialogTitle>
      <DialogDescription>
        We have emailed {invited.email} a link to set their password. It works once and expires on{' '}
        {new Date(invited.expiresAt).toLocaleDateString()}.
      </DialogDescription>

      <Alert tone="info" title="The link, in case the email does not arrive">
        <p className="break-all font-mono text-xs">{invited.invitationToken}</p>
        <p className="mt-2">
          This is shown once and never again — we only keep a hash of it. Send them another
          invitation if it is lost.
        </p>
      </Alert>

      <p className="text-sm text-muted-foreground">
        They cannot sell anything yet. Choose what they may sell and set a spending allowance on
        their page.
      </p>

      <DialogFooter>
        <Button onClick={onDone}>Done</Button>
      </DialogFooter>
    </div>
  );
}
