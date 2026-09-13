import { Users } from 'lucide-react';
import { Link } from 'react-router-dom';
import {
  Badge,
  Card,
  EmptyState,
  ErrorState,
  LoadingState,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@trips/ui';
import { formatMoneyShort } from '@trips/utils';
import { describeError } from '../../../api/errors';
import { PageHeader } from '../../../shell/page-header';
import { InviteSubAgentDialog } from '../components/invite-sub-agent-dialog';
import { useSubAgents } from '../subagents-api';
import { allowanceUsedPercent, statusLabel, statusTone, whyItCannotSell } from '../subagent-rules';
import type { SubAgent } from '../types';

/**
 * ============================================================================
 *  FRD §2.7 — the agents a principal has beneath it.
 * ============================================================================
 *
 * One row each, with the three things an owner actually asks about: can it sell
 * yet, what has it spent against what it was given, and does it see our
 * margins. Everything else is one click away on its own page.
 *
 * A sub-agent never reaches this screen — the route is hidden from one — but it
 * would be refused by the API anyway, which is the guard that matters.
 */
export function SubAgentsPage() {
  const network = useSubAgents();

  if (network.isPending) {
    return <LoadingState size="page" label="Loading your sub-agents" />;
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

  const { subAgents, maxSubAgents, canAddAnother, cannotAddReason } = network.data;

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title="Sub-agents"
        description="Agents who sell under your brand. You choose what each may sell, what it may see, and how much of your money it may spend."
        actions={
          <InviteSubAgentDialog canAddAnother={canAddAnother} cannotAddReason={cannotAddReason} />
        }
      />

      {maxSubAgents === null ? null : (
        <p className="text-sm text-muted-foreground">
          {subAgents.length} of {maxSubAgents} sub-agents on your plan.
        </p>
      )}

      {subAgents.length === 0 ? (
        <EmptyState icon={<Users aria-hidden />} title="No sub-agents yet">
          Invite an agent to sell under your brand. They get their own console, your prices, and
          only what you allow them to sell.
        </EmptyState>
      ) : (
        <Card className="overflow-x-auto p-0">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Agent</TableHead>
                <TableHead>Status</TableHead>
                <TableHead>Can sell</TableHead>
                <TableHead>Allowance used</TableHead>
                <TableHead>Sees margins</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {subAgents.map((subAgent) => (
                <SubAgentRow key={subAgent.id} subAgent={subAgent} />
              ))}
            </TableBody>
          </Table>
        </Card>
      )}
    </div>
  );
}

function SubAgentRow({ subAgent }: { subAgent: SubAgent }) {
  const blocker = whyItCannotSell(subAgent);

  return (
    <TableRow>
      <TableCell>
        <Link
          to={`/sub-agents/${subAgent.id}`}
          className="font-medium text-foreground underline-offset-4 hover:underline"
        >
          {subAgent.tradingName ?? subAgent.legalName}
        </Link>
        {subAgent.tradingName ? (
          <p className="text-xs text-muted-foreground">{subAgent.legalName}</p>
        ) : null}
      </TableCell>

      <TableCell>
        <Badge tone={statusTone(subAgent.status)}>{statusLabel(subAgent.status)}</Badge>
        {subAgent.statusReason ? (
          <p className="mt-1 max-w-xs text-xs text-muted-foreground">{subAgent.statusReason}</p>
        ) : null}
      </TableCell>

      <TableCell className="max-w-xs text-sm text-muted-foreground">
        {blocker ?? <span className="text-foreground">Yes</span>}
      </TableCell>

      <TableCell>
        <AllowanceCell subAgent={subAgent} />
      </TableCell>

      <TableCell>
        {subAgent.canSeeMargin ? (
          <span className="text-sm text-foreground">Yes</span>
        ) : (
          <Badge tone="neutral">Hidden</Badge>
        )}
      </TableCell>
    </TableRow>
  );
}

function AllowanceCell({ subAgent }: { subAgent: SubAgent }) {
  const { allowanceSpentMinor, allowanceLimitMinor, allowanceCurrency } = subAgent;

  if (allowanceLimitMinor === null || allowanceSpentMinor === null || allowanceCurrency === null) {
    return <span className="text-sm text-muted-foreground">None set</span>;
  }

  const used = allowanceUsedPercent(allowanceSpentMinor, allowanceLimitMinor);

  return (
    <div className="flex min-w-32 flex-col gap-1">
      <span className="text-sm text-foreground">
        {formatMoneyShort(allowanceSpentMinor, allowanceCurrency)} of{' '}
        {formatMoneyShort(allowanceLimitMinor, allowanceCurrency)}
      </span>

      {/* A plain bar rather than a charting library: one number, one length. */}
      <div
        className="h-1.5 w-full overflow-hidden rounded-full bg-muted"
        role="img"
        aria-label={`${used}% of the allowance used`}
      >
        <div
          className={used >= 80 ? 'h-full bg-destructive' : 'h-full bg-primary'}
          style={{ width: `${used}%` }}
        />
      </div>
    </div>
  );
}
