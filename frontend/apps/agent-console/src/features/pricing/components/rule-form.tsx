import { useState, type FormEvent, type ReactNode } from 'react';
import { Alert, Button, Input, SegmentedControl } from '@trips/ui';
import { describeError } from '../../../api/errors';
import {
  buildRuleRequest,
  draftFromRule,
  type CalculationType,
  type DraftErrors,
  type MarkupRule,
  type RuleSlot,
} from '../pricing-rules';
import { useSaveRule } from '../pricing-queries';

const CALCULATION_OPTIONS = [
  { value: 'Percentage', label: 'Percentage of net' },
  { value: 'Fixed', label: 'Fixed amount' },
] as const;

export interface RuleFormProps {
  currency: string;
  /** Where the rule goes. Null while the agent has not said which product yet. */
  slot: RuleSlot | null;
  /** Why `slot` is null — shown instead of saving. */
  slotProblem?: string;
  /** The rule being changed, if any. Saving replaces it; it is never edited in place. */
  replacing?: MarkupRule;
  /** Fields that choose the slot, e.g. which product. Rendered first. */
  children?: ReactNode;
  onDone: () => void;
}

/** One markup rule's terms: a percentage with optional caps, or a fixed amount. */
export function RuleForm({
  currency,
  slot,
  slotProblem,
  replacing,
  children,
  onDone,
}: RuleFormProps) {
  const [draft, setDraft] = useState(() => draftFromRule(replacing));
  const [errors, setErrors] = useState<DraftErrors>({});
  const [slotError, setSlotError] = useState<string | undefined>();
  const save = useSaveRule();

  function submit(event: FormEvent) {
    event.preventDefault();

    if (slot === null) {
      setSlotError(slotProblem ?? 'Choose what this rule applies to.');
      return;
    }
    setSlotError(undefined);

    const built = buildRuleRequest(draft, slot, currency, replacing);
    if (!built.ok) {
      setErrors(built.errors);
      return;
    }

    setErrors({});
    save.mutate({ request: built.request, replacing }, { onSuccess: onDone });
  }

  const update = (field: keyof typeof draft) => (value: string) =>
    setDraft((current) => ({ ...current, [field]: value }));

  return (
    <form onSubmit={submit} className="flex flex-col gap-4" noValidate>
      {children}
      {slotError ? (
        <p className="text-sm text-destructive" role="alert">
          {slotError}
        </p>
      ) : null}

      <SegmentedControl
        label="How the markup is worked out"
        options={CALCULATION_OPTIONS}
        value={draft.calculationType}
        onChange={(value: CalculationType) =>
          setDraft((current) => ({ ...current, calculationType: value }))
        }
      />

      {draft.calculationType === 'Percentage' ? (
        <div className="grid gap-4 sm:grid-cols-3">
          <Input
            label="Markup"
            inputMode="decimal"
            value={draft.percent}
            onChange={(event) => update('percent')(event.target.value)}
            error={errors.percent}
            trailing={<span className="px-2 text-sm text-muted-foreground">%</span>}
          />
          <Input
            label="At least (optional)"
            inputMode="decimal"
            value={draft.minCap}
            onChange={(event) => update('minCap')(event.target.value)}
            error={errors.minCap}
            hint={`In ${currency}. Blank means no minimum.`}
          />
          <Input
            label="At most (optional)"
            inputMode="decimal"
            value={draft.maxCap}
            onChange={(event) => update('maxCap')(event.target.value)}
            error={errors.maxCap}
            hint={`In ${currency}. Blank means no maximum.`}
          />
        </div>
      ) : (
        <div className="grid gap-4 sm:grid-cols-3">
          <Input
            label="Amount added"
            inputMode="decimal"
            value={draft.amount}
            onChange={(event) => update('amount')(event.target.value)}
            error={errors.amount}
            hint={`In ${currency}, added to every sale.`}
          />
        </div>
      )}

      {replacing ? (
        <p className="text-xs text-muted-foreground">
          Saving starts a new rule from now on and retires this one. Anything already quoted or
          booked keeps the price it was given.
        </p>
      ) : null}

      {save.isError ? (
        <Alert tone="destructive" title={describeError(save.error).title}>
          {describeError(save.error).detail}
        </Alert>
      ) : null}

      <div className="flex flex-wrap gap-2">
        <Button type="submit" loading={save.isPending}>
          Save rule
        </Button>
        <Button type="button" variant="ghost" onClick={onDone} disabled={save.isPending}>
          Cancel
        </Button>
      </div>
    </form>
  );
}
