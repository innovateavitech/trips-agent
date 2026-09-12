import { Badge, Button, Card, Skeleton } from '@trips/ui';
import { formatMoney } from '@trips/utils';
import { planStatusDisplay } from '../billing-rules';
import type { MyPlan } from '../types';
import { formatDate } from '../format';

/**
 * The plan this agency is on, at the top of the screen.
 *
 * Four facts, in the order somebody asks them: what am I on, what does it cost, when is the next
 * charge, and what is it charged to. The card on file is described and never quoted in full —
 * there is no card number anywhere in this console to quote.
 */
export function PlanSummary({
  plan,
  onChangePlan,
  onCancelScheduled,
  cancelling,
}: {
  plan: MyPlan;
  onChangePlan: () => void;
  onCancelScheduled: (migrationId: string) => void;
  cancelling: boolean;
}) {
  const status = planStatusDisplay(plan.status);

  return (
    <Card className="flex flex-col gap-5 p-6">
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div className="flex flex-col gap-1">
          <div className="flex flex-wrap items-center gap-2">
            <h2 className="text-xl font-semibold text-foreground">{plan.planName}</h2>
            <Badge tone={status.tone}>{status.label}</Badge>
          </div>
          <p className="max-w-xl text-sm text-muted-foreground">
            {plan.statusReason ?? status.meaning}
          </p>
        </div>

        <div className="text-right">
          <p className="text-2xl font-semibold text-foreground">
            {plan.amountMinor === null ? 'Free' : formatMoney(plan.amountMinor, plan.currency)}
          </p>
          {plan.amountMinor === null ? null : (
            <p className="text-xs text-muted-foreground">per month</p>
          )}
        </div>
      </div>

      <dl className="grid gap-4 sm:grid-cols-3">
        <Fact
          label={plan.status === 'Trialing' ? 'Trial ends' : 'Next charge'}
          value={
            plan.status === 'Trialing'
              ? formatDate(plan.trialEndsAt)
              : plan.nextChargeAt
                ? formatDate(plan.nextChargeAt)
                : 'Nothing to charge'
          }
        />
        <Fact label="Card on file" value={plan.cardOnFile ?? 'None yet'} />
        <Fact label="This period" value={period(plan)} />
      </dl>

      {plan.features.length > 0 ? (
        <div className="flex flex-col gap-2 rounded-lg bg-muted p-4">
          <p className="text-xs font-medium text-foreground">What your plan includes</p>
          <ul className="grid gap-1 sm:grid-cols-2">
            {plan.features.map((feature) => (
              <li key={feature.code} className="flex items-baseline justify-between gap-2 text-sm">
                <span className="text-muted-foreground">{feature.name}</span>
                <span className="font-medium text-foreground">{feature.display}</span>
              </li>
            ))}
          </ul>
        </div>
      ) : null}

      {plan.scheduledChange ? (
        <div className="flex flex-col gap-2 rounded-lg border border-border p-4">
          <p className="text-sm font-medium text-foreground">
            Your plan changes to {plan.scheduledChange.toPlan} on{' '}
            {formatDate(plan.scheduledChange.effectiveAt)}
          </p>
          <p className="text-sm text-muted-foreground">{plan.scheduledChange.explanation}</p>

          {plan.scheduledChange.canCancel ? (
            <div>
              <Button
                variant="outline"
                size="sm"
                loading={cancelling}
                onClick={() => onCancelScheduled(plan.scheduledChange!.migrationId)}
              >
                Keep my current plan instead
              </Button>
            </div>
          ) : null}
        </div>
      ) : null}

      <div>
        <Button onClick={onChangePlan}>
          {plan.subscriptionId === null ? 'Choose a plan' : 'Change plan'}
        </Button>
      </div>
    </Card>
  );
}

function period(plan: MyPlan): string {
  if (!plan.currentPeriodStart || !plan.currentPeriodEnd) return 'Not started';
  return `${formatDate(plan.currentPeriodStart)} – ${formatDate(plan.currentPeriodEnd)}`;
}

function Fact({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex flex-col">
      <dt className="text-xs text-muted-foreground">{label}</dt>
      <dd className="text-sm font-medium text-foreground">{value}</dd>
    </div>
  );
}

export function PlanSummarySkeleton() {
  return (
    <Card className="flex flex-col gap-5 p-6" aria-busy="true">
      <Skeleton className="h-7 w-48" />
      <Skeleton className="h-4 w-full max-w-md" />
      <Skeleton className="h-20 w-full" />
      <Skeleton className="h-9 w-32" />
    </Card>
  );
}
