import { useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import {
  Alert,
  Badge,
  Button,
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogTitle,
  ErrorState,
  Input,
  LoadingState,
} from '@trips/ui';
import { describeError } from '../../../api/errors';
import { PageHeader } from '../../../shell/page-header';
import { AllowanceCard } from '../components/allowance-card';
import { PermissionsMatrix } from '../components/permissions-matrix';
import { ScopesEditor } from '../components/scopes-editor';
import { useChangeStanding, useSubAgents } from '../subagents-api';
import { canChangeStanding, statusLabel, statusTone, whyItCannotSell } from '../subagent-rules';
import type { SubAgent } from '../types';

/**
 * One sub-agent: what it may sell, what it may do, what it may spend, and
 * whether it is still trading.
 *
 * The three cards are in the order an owner sets them up, and the banner at the
 * top says the one thing standing between this agent and its first sale.
 */
export function SubAgentDetailPage() {
  const { subAgencyId = '' } = useParams();
  const network = useSubAgents();

  if (network.isPending) {
    return <LoadingState size="page" label="Loading this sub-agent" />;
  }

  if (network.isError) {
    const problem = describeError(network.error);

    return (
      <ErrorState
        title={problem.title}
        detail={problem.detail}
        onRetry={() => void network.refetch()}
        retrying={network.isFetching}
      />
    );
  }

  const subAgent = network.data.subAgents.find((candidate) => candidate.id === subAgencyId);

  if (!subAgent) {
    return (
      <ErrorState
        title="That sub-agent is not one of yours"
        detail="It may have been ended, or the link may be wrong."
      />
    );
  }

  const blocker = whyItCannotSell(subAgent);
  const ended = subAgent.status === 'Terminated';

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title={subAgent.tradingName ?? subAgent.legalName}
        description={
          <>
            {subAgent.legalName} ·{' '}
            <Link to="/sub-agents" className="underline underline-offset-4">
              back to sub-agents
            </Link>
          </>
        }
        actions={
          <div className="flex flex-wrap items-center gap-2">
            <Badge tone={statusTone(subAgent.status)}>{statusLabel(subAgent.status)}</Badge>
            {canChangeStanding(subAgent) ? <StandingActions subAgent={subAgent} /> : null}
          </div>
        }
      />

      {blocker ? (
        <Alert tone={ended ? 'destructive' : 'warning'} title="They cannot sell yet">
          {blocker}
          {subAgent.statusReason ? ` Reason given: ${subAgent.statusReason}` : ''}
        </Alert>
      ) : null}

      <ScopesEditor subAgencyId={subAgent.id} readOnly={ended} />
      <AllowanceCard subAgencyId={subAgent.id} readOnly={ended} />
      <PermissionsMatrix subAgencyId={subAgent.id} readOnly={ended} />
    </div>
  );
}

/**
 * Freeze, unfreeze and revoke. Each asks for a reason, because each is written
 * to the audit log and a record saying only "frozen" answers nothing later.
 */
function StandingActions({ subAgent }: { subAgent: SubAgent }) {
  const [action, setAction] = useState<'freeze' | 'unfreeze' | 'revoke' | null>(null);
  const [reason, setReason] = useState('');
  const change = useChangeStanding();

  const frozen = subAgent.status === 'Suspended';

  const copy = {
    freeze: {
      title: 'Freeze this sub-agent',
      description:
        'They keep their sign-in and can read everything, but cannot book or spend your money until you lift it. Bookings they have already made are untouched.',
      confirm: 'Freeze them',
    },
    unfreeze: {
      title: 'Lift the freeze',
      description: 'They can sell again, and their allowance can be drawn on again.',
      confirm: 'Lift it',
    },
    revoke: {
      title: 'End this sub-agent for good',
      description:
        'Their sessions stop working, their invitations are cancelled and their allowance goes to zero. Bookings they made stay readable to you. This cannot be undone.',
      confirm: 'End the relationship',
    },
  } as const;

  return (
    <>
      {frozen ? (
        <Button variant="outline" size="sm" onClick={() => setAction('unfreeze')}>
          Lift the freeze
        </Button>
      ) : (
        <Button variant="outline" size="sm" onClick={() => setAction('freeze')}>
          Freeze
        </Button>
      )}

      <Button variant="destructive" size="sm" onClick={() => setAction('revoke')}>
        End
      </Button>

      <Dialog
        open={action !== null}
        onOpenChange={(open) => {
          if (!open) {
            setAction(null);
            setReason('');
            change.reset();
          }
        }}
      >
        <DialogContent>
          {action === null ? null : (
            <form
              className="flex flex-col gap-4"
              onSubmit={(event) => {
                event.preventDefault();

                change.mutate(
                  { id: subAgent.id, action, reason: reason.trim() },
                  {
                    onSuccess: () => {
                      setAction(null);
                      setReason('');
                    },
                  },
                );
              }}
            >
              <DialogTitle>{copy[action].title}</DialogTitle>
              <DialogDescription>{copy[action].description}</DialogDescription>

              {change.isError ? (
                <Alert tone="destructive" title={describeError(change.error).title}>
                  {describeError(change.error).detail}
                </Alert>
              ) : null}

              <Input
                label="Why"
                hint="Written to the audit log. Somebody will ask in six months."
                value={reason}
                onChange={(event) => setReason(event.target.value)}
                required
                autoFocus
              />

              <DialogFooter>
                <Button
                  type="button"
                  variant="outline"
                  onClick={() => {
                    setAction(null);
                    setReason('');
                  }}
                >
                  Cancel
                </Button>
                <Button
                  type="submit"
                  variant={action === 'revoke' ? 'destructive' : 'primary'}
                  disabled={change.isPending}
                >
                  {copy[action].confirm}
                </Button>
              </DialogFooter>
            </form>
          )}
        </DialogContent>
      </Dialog>
    </>
  );
}
