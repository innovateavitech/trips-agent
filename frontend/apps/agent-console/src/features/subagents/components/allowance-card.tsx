import { useEffect, useState } from 'react';
import {
  Alert,
  Badge,
  Button,
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
  ErrorState,
  Input,
  LoadingState,
  Select,
} from '@trips/ui';
import { formatMoney } from '@trips/utils';
import { describeError } from '../../../api/errors';
import { useAllowance, useChangeAllowance } from '../subagents-api';
import { allowanceUsedPercent, isAllowanceNearlySpent } from '../subagent-rules';
import type { AllowancePeriod } from '../types';

const PERIODS: { value: AllowancePeriod; label: string }[] = [
  { value: 'Monthly', label: 'Every month' },
  { value: 'Weekly', label: 'Every week' },
  { value: 'Daily', label: 'Every day' },
  { value: 'Lifetime', label: 'Once — never starts again' },
];

/**
 * The hard cap on what a sub-agent may spend out of your money.
 *
 * It is a cap, not a wallet: there is nothing to top up here and nothing to
 * withdraw. Every booking the sub-agent makes is counted against it first, and
 * the booking is refused the moment the next one would take it past the limit —
 * so two bookings at the same instant can never both slip through.
 *
 * Lowering a limit below what has already been spent is allowed and takes
 * nothing back. The money is spent; it simply stops the next booking.
 */
export function AllowanceCard({
  subAgencyId,
  readOnly,
}: {
  subAgencyId: string;
  readOnly: boolean;
}) {
  const allowance = useAllowance(subAgencyId);
  const change = useChangeAllowance(subAgencyId);

  const [limit, setLimit] = useState('');
  const [period, setPeriod] = useState<AllowancePeriod>('Monthly');

  // Seeded from the server once it answers, and left alone afterwards so typing
  // is never interrupted by a refetch.
  useEffect(() => {
    if (allowance.data) {
      setLimit(String(allowance.data.limitMinor / 100));
      setPeriod(allowance.data.period);
    }
  }, [allowance.data]);

  if (allowance.isPending) {
    return <LoadingState label="Loading the allowance" />;
  }

  if (allowance.isError) {
    const problem = describeError(allowance.error);

    return (
      <ErrorState
        title={problem.title}
        detail={problem.detail}
        onRetry={() => void allowance.refetch()}
        retrying={allowance.isFetching}
      />
    );
  }

  const current = allowance.data;
  const currency = current?.currency ?? 'NGN';
  const used = current ? allowanceUsedPercent(current.spentMinor, current.limitMinor) : 0;

  return (
    <Card>
      <CardHeader>
        <CardTitle>Spending allowance</CardTitle>
        <CardDescription>
          The most they may spend of your money in a period. Bookings past it are refused before
          anything is booked or charged.
        </CardDescription>
      </CardHeader>

      <CardContent className="flex flex-col gap-4">
        {change.isError ? (
          <Alert tone="destructive" title={describeError(change.error).title}>
            {describeError(change.error).detail}
          </Alert>
        ) : null}

        {current === null ? (
          <Alert tone="warning" title="They have no allowance, so they cannot spend anything">
            Set one below. Until you do, every booking they attempt is refused.
          </Alert>
        ) : (
          <div className="flex flex-col gap-3">
            <div className="flex flex-wrap items-baseline justify-between gap-2">
              <p className="text-sm text-foreground">
                <span className="text-2xl font-semibold">
                  {formatMoney(current.remainingMinor, currency)}
                </span>{' '}
                left of {formatMoney(current.limitMinor, currency)}
              </p>

              {current.status === 'Frozen' ? <Badge tone="destructive">Frozen</Badge> : null}
            </div>

            <div
              className="h-2 w-full overflow-hidden rounded-full bg-muted"
              role="img"
              aria-label={`${used}% of the allowance used`}
            >
              <div
                className={used >= 80 ? 'h-full bg-destructive' : 'h-full bg-primary'}
                style={{ width: `${used}%` }}
              />
            </div>

            <p className="text-xs text-muted-foreground">
              {formatMoney(current.spentMinor, currency)} spent this period
              {current.resetsAt === null
                ? '. This allowance never starts again — raise the limit to give them more.'
                : `. Starts again on ${new Date(current.resetsAt).toLocaleDateString()}.`}
            </p>

            {isAllowanceNearlySpent(current) ? (
              <Alert tone="warning" title="They are close to their limit">
                Their next few bookings may be refused. Raise the limit if that is not what you
                want.
              </Alert>
            ) : null}
          </div>
        )}

        {readOnly ? null : (
          <form
            className="flex flex-wrap items-end gap-2 border-t border-border pt-4"
            onSubmit={(event) => {
              event.preventDefault();

              const naira = Number(limit);
              if (!Number.isFinite(naira) || naira < 0) return;

              // Typed in naira, sent in kobo. Rounded here so a stray "1000.005"
              // cannot become a fraction of a kobo on its way to the server.
              change.mutate({ limitMinor: Math.round(naira * 100), period });
            }}
          >
            <Input
              label={`Limit (${currency})`}
              type="number"
              min="0"
              step="0.01"
              inputMode="decimal"
              value={limit}
              onChange={(event) => setLimit(event.target.value)}
              required
              className="w-40"
            />

            <Select
              label="Starts again"
              value={period}
              onChange={(event) => setPeriod(event.target.value as AllowancePeriod)}
            >
              {PERIODS.map((option) => (
                <option key={option.value} value={option.value}>
                  {option.label}
                </option>
              ))}
            </Select>

            <Button type="submit" disabled={change.isPending}>
              {current === null ? 'Set the allowance' : 'Save'}
            </Button>

            {current === null ? null : (
              <Button
                type="button"
                variant="outline"
                disabled={change.isPending}
                onClick={() => change.mutate({ freeze: current.status === 'Active' })}
              >
                {current.status === 'Active' ? 'Freeze it' : 'Unfreeze it'}
              </Button>
            )}
          </form>
        )}
      </CardContent>
    </Card>
  );
}
