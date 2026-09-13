import { useState } from 'react';
import {
  Card,
  ErrorState,
  Input,
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
import { useNetworkPerformance } from '../subagents-api';
import {
  allowanceUsedPercent,
  defaultRange,
  fromDateInput,
  statusLabel,
  toDateInput,
} from '../subagent-rules';
import type { NetworkPerformance } from '../types';

/**
 * ============================================================================
 *  What the whole network sold, and who sold it.
 * ============================================================================
 *
 * **The margin column is driven by the response, not by a permission check
 * here.** The API answers with one of two shapes: with margin, or without it
 * entirely — not with a null. So this screen asks "did a margin arrive?" and
 * shows the column if it did. A browser-side check would be a second place for
 * the rule to live, and the one that silently disagrees.
 */
export function NetworkPerformancePage() {
  const [range, setRange] = useState(defaultRange);
  const performance = useNetworkPerformance(range.from, range.to);

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title="Network performance"
        description="What you and the agents beneath you have sold, and how much of their allowances they have used."
        actions={
          <div className="flex flex-wrap items-end gap-2">
            <Input
              label="From"
              type="date"
              value={toDateInput(range.from)}
              max={toDateInput(range.to)}
              onChange={(event) =>
                setRange((current) => ({ ...current, from: fromDateInput(event.target.value) }))
              }
            />
            <Input
              label="To"
              type="date"
              value={toDateInput(range.to)}
              min={toDateInput(range.from)}
              onChange={(event) =>
                setRange((current) => ({
                  ...current,
                  to: fromDateInput(event.target.value, true),
                }))
              }
            />
          </div>
        }
      />

      {performance.isPending ? (
        <LoadingState size="page" label="Adding up your network" />
      ) : performance.isError ? (
        <ErrorState
          title={describeError(performance.error).title}
          detail={describeError(performance.error).detail}
          onRetry={() => void performance.refetch()}
          retrying={performance.isFetching}
        />
      ) : (
        <NetworkTotals performance={performance.data} />
      )}
    </div>
  );
}

function NetworkTotals({ performance }: { performance: NetworkPerformance }) {
  // The whole margin-visibility rule on this screen, in one line.
  const showsMargin = performance.marginMinor !== null;
  const { currency } = performance;

  return (
    <div className="flex flex-col gap-6">
      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
        <Totals label="Bookings" value={String(performance.orders)} />
        <Totals label="Sales" value={formatMoneyShort(performance.salesMinor, currency)} />
        {showsMargin ? (
          <Totals
            label="Margin"
            value={formatMoneyShort(performance.marginMinor ?? 0, currency)}
            hint="Your markup, less the platform's fee."
          />
        ) : null}
      </div>

      <Card className="overflow-x-auto p-0">
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>Agency</TableHead>
              <TableHead>Bookings</TableHead>
              <TableHead>Sales</TableHead>
              {showsMargin ? <TableHead>Margin</TableHead> : null}
              <TableHead>Allowance used</TableHead>
            </TableRow>
          </TableHeader>

          <TableBody>
            {performance.members.map((member) => (
              <TableRow key={member.agencyId}>
                <TableCell>
                  <span className="font-medium text-foreground">{member.name}</span>
                  <p className="text-xs text-muted-foreground">
                    {member.isPrincipal ? 'You' : statusLabel(member.status)}
                  </p>
                </TableCell>

                <TableCell>{member.orders}</TableCell>
                <TableCell>{formatMoneyShort(member.salesMinor, currency)}</TableCell>

                {showsMargin ? (
                  <TableCell>{formatMoneyShort(member.marginMinor ?? 0, currency)}</TableCell>
                ) : null}

                <TableCell>
                  {member.allowanceLimitMinor === null || member.allowanceSpentMinor === null ? (
                    <span className="text-sm text-muted-foreground">
                      {member.isPrincipal ? '—' : 'None set'}
                    </span>
                  ) : (
                    <span className="text-sm text-foreground">
                      {formatMoneyShort(member.allowanceSpentMinor, currency)} of{' '}
                      {formatMoneyShort(member.allowanceLimitMinor, currency)} (
                      {allowanceUsedPercent(member.allowanceSpentMinor, member.allowanceLimitMinor)}
                      %)
                    </span>
                  )}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </Card>
    </div>
  );
}

function Totals({ label, value, hint }: { label: string; value: string; hint?: string }) {
  return (
    <Card className="flex flex-col gap-1 p-4">
      <p className="text-sm text-muted-foreground">{label}</p>
      <p className="text-2xl font-semibold tracking-tight text-foreground">{value}</p>
      {hint ? <p className="text-xs text-muted-foreground">{hint}</p> : null}
    </Card>
  );
}
