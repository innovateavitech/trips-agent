import { useState } from 'react';
import {
  Alert,
  Badge,
  Button,
  Card,
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogTitle,
} from '@trips/ui';
import { formatMoney } from '@trips/utils';
import { planActionLabel, planChangeConsequences, planChangeKind } from '../billing-rules';
import type { MyPlan, Plan } from '../types';

/**
 * The plans on offer, and what choosing one commits the agency to.
 *
 * Every card says what happens before it happens, because the two directions are not symmetrical:
 * an upgrade is charged today, and a downgrade waits for the end of the period already paid for.
 * A button labelled "switch" for both would be wrong half the time.
 */
export function PlanPicker({
  open,
  onOpenChange,
  plans,
  current,
  onChoose,
  pending,
  error,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  plans: Plan[];
  current: MyPlan;
  onChoose: (plan: Plan) => void;
  pending: boolean;
  error: unknown;
}) {
  const [confirming, setConfirming] = useState<Plan | null>(null);

  const kind = confirming ? planChangeKind(confirming, current) : 'current';

  return (
    <Dialog
      open={open}
      onOpenChange={(next) => {
        if (!next) setConfirming(null);
        onOpenChange(next);
      }}
    >
      <DialogContent aria-describedby={undefined}>
        <DialogTitle>{confirming ? `Move to ${confirming.name}` : 'Choose a plan'}</DialogTitle>
        <DialogDescription>
          {confirming
            ? planChangeConsequences(confirming, kind)
            : 'You can change again later. Nothing you have already built is ever removed by a change of plan.'}
        </DialogDescription>

        {error ? (
          <Alert tone="destructive" title="That did not go through">
            {describe(error)}
          </Alert>
        ) : null}

        {confirming ? (
          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => setConfirming(null)}>
              Back to the plans
            </Button>
            <Button type="button" loading={pending} onClick={() => onChoose(confirming)}>
              {kind === 'upgrade' ? 'Continue to payment' : 'Schedule the change'}
            </Button>
          </DialogFooter>
        ) : (
          <div className="flex max-h-[28rem] flex-col gap-3 overflow-y-auto">
            {plans.map((plan) => (
              <PlanCard
                key={plan.tierId}
                plan={plan}
                current={current}
                onChoose={() => setConfirming(plan)}
              />
            ))}
          </div>
        )}
      </DialogContent>
    </Dialog>
  );
}

function PlanCard({
  plan,
  current,
  onChoose,
}: {
  plan: Plan;
  current: MyPlan;
  onChoose: () => void;
}) {
  const kind = planChangeKind(plan, current);

  return (
    <Card className="flex flex-col gap-3 p-4">
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div className="flex flex-col gap-1">
          <div className="flex flex-wrap items-center gap-2">
            <h3 className="text-base font-semibold text-foreground">{plan.name}</h3>
            {plan.isCurrent ? <Badge tone="primary">Your plan</Badge> : null}
            {plan.trialDays > 0 && !plan.isCurrent ? (
              <Badge tone="info">{plan.trialDays}-day free trial</Badge>
            ) : null}
          </div>
          {plan.description ? (
            <p className="text-sm text-muted-foreground">{plan.description}</p>
          ) : null}
        </div>

        <p className="text-lg font-semibold text-foreground">
          {plan.amountMinor === null || plan.amountMinor === 0
            ? 'Free'
            : `${formatMoney(plan.amountMinor, plan.currency)}/mo`}
        </p>
      </div>

      <ul className="grid gap-1 sm:grid-cols-2">
        {plan.features.map((feature) => (
          <li key={feature.code} className="flex items-baseline justify-between gap-2 text-xs">
            <span className="text-muted-foreground">{feature.name}</span>
            <span className="font-medium text-foreground">{feature.display}</span>
          </li>
        ))}
      </ul>

      <div>
        <Button
          size="sm"
          variant={kind === 'upgrade' ? 'primary' : 'outline'}
          disabled={plan.isCurrent}
          onClick={onChoose}
        >
          {planActionLabel(kind)}
        </Button>
      </div>
    </Card>
  );
}

function describe(error: unknown): string {
  return error instanceof Error && error.message.length > 0
    ? error.message
    : 'Something went wrong and nothing has been charged. Try again in a moment.';
}
