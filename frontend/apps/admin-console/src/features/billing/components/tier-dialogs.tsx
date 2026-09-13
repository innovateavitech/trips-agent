import { useEffect, useState, type FormEvent } from 'react';
import {
  Alert,
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
import { formatMoney } from '../../../lib/format';
import { MAX_REASON_LENGTH, validateReason } from '../../../lib/reason';
import {
  currentPrice,
  describeEntitlementValue,
  grantsOf,
  parseEntitlementValue,
} from '../billing-rules';
import type {
  EntitlementCatalogueItem,
  MigrateSubscribersRequest,
  SaveTierRequest,
  SetTierEntitlementsRequest,
  SetTierPriceRequest,
  Tier,
} from '../types';

/**
 * Creating a plan, or renaming one.
 *
 * The code is set once and never again: it appears in reports and in every invoice's audit trail,
 * so a plan that changes its code is a plan whose history stops joining up.
 */
export function TierFormDialog({
  open,
  onOpenChange,
  tier,
  onSubmit,
  pending,
  error,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  /** The plan being edited, or undefined when creating one. */
  tier?: Tier;
  onSubmit: (request: SaveTierRequest) => void;
  pending: boolean;
  error: unknown;
}) {
  const [code, setCode] = useState('');
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [trialDays, setTrialDays] = useState('0');
  const [sortOrder, setSortOrder] = useState('0');
  const [isFallback, setIsFallback] = useState(false);
  const [reason, setReason] = useState('');
  const [reasonProblem, setReasonProblem] = useState<string>();

  // Filled as it opens, cleared as it closes — so reopening to finish a half-typed sentence does
  // not wipe it.
  useEffect(() => {
    if (open) {
      setCode(tier?.code ?? '');
      setName(tier?.name ?? '');
      setDescription(tier?.customerDescription ?? '');
      setTrialDays(String(tier?.trialDays ?? 0));
      setSortOrder(String(tier?.sortOrder ?? 0));
      setIsFallback(tier?.isFallback ?? false);
      return;
    }

    setReason('');
    setReasonProblem(undefined);
  }, [open, tier]);

  function submit(event: FormEvent) {
    event.preventDefault();

    const invalid = validateReason(reason);
    setReasonProblem(invalid);

    if (invalid) return;

    onSubmit({
      code: code.trim().toLowerCase(),
      name: name.trim(),
      customerDescription: description.trim() === '' ? null : description.trim(),
      trialDays: Number.parseInt(trialDays, 10) || 0,
      sortOrder: Number.parseInt(sortOrder, 10) || 0,
      isFallback,
      reason: reason.trim(),
    });
  }

  const failure = error ? describeLoadError(error) : null;

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent aria-describedby={undefined}>
        <DialogTitle>{tier ? `Edit ${tier.name}` : 'Create a plan'}</DialogTitle>
        <DialogDescription>
          {tier
            ? 'The code stays as it is — reports and invoices point at it.'
            : 'It starts as a draft. Nobody can subscribe until you publish it.'}
        </DialogDescription>

        {failure ? (
          <Alert tone="destructive" title={failure.title}>
            {failure.detail}
          </Alert>
        ) : null}

        <form className="flex flex-col gap-4" onSubmit={submit}>
          <div className="grid gap-3 sm:grid-cols-2">
            <Input
              label="Name"
              required
              value={name}
              error={fieldError(error, 'name')}
              hint="What an agency sees in the plan picker."
              onChange={(event) => setName(event.target.value)}
            />
            <Input
              label="Code"
              required
              disabled={tier !== undefined}
              value={code}
              error={fieldError(error, 'code')}
              hint={
                tier
                  ? 'Set when the plan was created.'
                  : 'Lowercase, no spaces: growth, enterprise.'
              }
              onChange={(event) => setCode(event.target.value)}
            />
          </div>

          <Textarea
            label="Description"
            rows={2}
            value={description}
            error={fieldError(error, 'customerDescription')}
            hint="The sentence an agency reads when choosing. Optional."
            onChange={(event) => setDescription(event.target.value)}
          />

          <div className="grid gap-3 sm:grid-cols-2">
            <Input
              label="Trial days"
              type="number"
              min={0}
              max={90}
              value={trialDays}
              error={fieldError(error, 'trialDays')}
              hint="0 for no trial. 90 at the most."
              onChange={(event) => setTrialDays(event.target.value)}
            />
            <Input
              label="Sort order"
              type="number"
              value={sortOrder}
              error={fieldError(error, 'sortOrder')}
              hint="Lower appears first in the picker."
              onChange={(event) => setSortOrder(event.target.value)}
            />
          </div>

          <label className="flex items-start gap-3 rounded-md bg-muted p-3">
            <input
              type="checkbox"
              className="mt-1 h-4 w-4 accent-primary"
              checked={isFallback}
              onChange={(event) => setIsFallback(event.target.checked)}
            />
            <span className="flex flex-col gap-1">
              <span className="text-sm font-medium text-foreground">
                This is the free fallback plan
              </span>
              <span className="text-xs text-muted-foreground">
                Where an agency lands when its payments keep failing. Only one plan can be the
                fallback; choosing this one takes it off whichever plan has it now. Without a
                fallback, a failed payment ends in suspension instead.
              </span>
            </span>
          </label>

          <Textarea
            label="Why"
            rows={3}
            required
            maxLength={MAX_REASON_LENGTH}
            value={reason}
            error={reasonProblem}
            hint="Recorded against your name, and kept for seven years."
            onChange={(event) => setReason(event.target.value)}
          />

          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
              Cancel
            </Button>
            <Button type="submit" loading={pending}>
              {tier ? 'Save the plan' : 'Create the plan'}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

/**
 * Setting what a plan costs.
 *
 * Amounts are typed in whole naira and sent in kobo. The conversion happens once, here, at the edge
 * — everything behind this point is minor units, and every bug in this area comes from converting
 * in one more place than necessary.
 */
export function TierPriceDialog({
  open,
  onOpenChange,
  tier,
  onSubmit,
  pending,
  error,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  tier: Tier;
  onSubmit: (request: SetTierPriceRequest) => void;
  pending: boolean;
  error: unknown;
}) {
  const existing = currentPrice(tier);

  const [major, setMajor] = useState('');
  const [reason, setReason] = useState('');
  const [reasonProblem, setReasonProblem] = useState<string>();
  const [amountProblem, setAmountProblem] = useState<string>();

  useEffect(() => {
    if (open) {
      setMajor(existing ? String(existing.amountMinor / 100) : '');
      return;
    }

    setReason('');
    setReasonProblem(undefined);
    setAmountProblem(undefined);
  }, [open, existing]);

  function submit(event: FormEvent) {
    event.preventDefault();

    const amountMinor = Math.round(Number.parseFloat(major) * 100);
    const badAmount =
      Number.isNaN(amountMinor) || amountMinor < 0 ? 'A price of zero or more.' : undefined;
    const invalid = validateReason(reason);

    setAmountProblem(badAmount);
    setReasonProblem(invalid);

    if (badAmount || invalid) return;

    onSubmit({ currency: 'NGN', interval: 'Monthly', amountMinor, reason: reason.trim() });
  }

  const failure = error ? describeLoadError(error) : null;

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent aria-describedby={undefined}>
        <DialogTitle>Price {tier.name}</DialogTitle>
        <DialogDescription>
          Monthly, in naira. Agencies already on this plan keep the price they agreed — moving them
          is a separate, deliberate step with thirty days&rsquo; notice.
        </DialogDescription>

        {failure ? (
          <Alert tone="destructive" title={failure.title}>
            {failure.detail}
          </Alert>
        ) : null}

        <form className="flex flex-col gap-4" onSubmit={submit}>
          <Input
            label="Price per month"
            type="number"
            min={0}
            step="0.01"
            required
            value={major}
            error={amountProblem ?? fieldError(error, 'amountMinor')}
            hint={existing ? `Currently ${formatMoney(existing.amountMinor)}.` : 'No price yet.'}
            onChange={(event) => setMajor(event.target.value)}
          />

          <Textarea
            label="Why"
            rows={3}
            required
            maxLength={MAX_REASON_LENGTH}
            value={reason}
            error={reasonProblem}
            onChange={(event) => setReason(event.target.value)}
          />

          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
              Cancel
            </Button>
            <Button type="submit" loading={pending}>
              Set the price
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

/**
 * Setting what a plan unlocks.
 *
 * Every entitlement in the catalogue is listed, whether the plan grants it or not, showing the
 * value that would apply. A blank row would leave an admin guessing which way the blank goes, and
 * the answer — the restrictive one — is the whole point.
 */
export function TierEntitlementsDialog({
  open,
  onOpenChange,
  tier,
  catalogue,
  onSubmit,
  pending,
  error,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  tier: Tier;
  catalogue: EntitlementCatalogueItem[];
  onSubmit: (request: SetTierEntitlementsRequest) => void;
  pending: boolean;
  error: unknown;
}) {
  const [values, setValues] = useState<Record<string, string>>({});
  const [problems, setProblems] = useState<Record<string, string>>({});
  const [reason, setReason] = useState('');
  const [reasonProblem, setReasonProblem] = useState<string>();

  useEffect(() => {
    if (open) {
      setValues(
        Object.fromEntries(grantsOf(tier, catalogue).map((grant) => [grant.code, grant.value])),
      );
      setProblems({});
      return;
    }

    setReason('');
    setReasonProblem(undefined);
  }, [open, tier, catalogue]);

  function submit(event: FormEvent) {
    event.preventDefault();

    const nextProblems: Record<string, string> = {};
    const entitlements: { code: string; value: string }[] = [];

    for (const item of catalogue) {
      const parsed = parseEntitlementValue(item.valueType, values[item.code] ?? item.defaultValue);

      if ('problem' in parsed) {
        nextProblems[item.code] = parsed.problem;
        continue;
      }

      entitlements.push({ code: item.code, value: parsed.value });
    }

    const invalid = validateReason(reason);

    setProblems(nextProblems);
    setReasonProblem(invalid);

    if (Object.keys(nextProblems).length > 0 || invalid) return;

    onSubmit({ entitlements, reason: reason.trim() });
  }

  const failure = error ? describeLoadError(error) : null;

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent aria-describedby={undefined}>
        <DialogTitle>What {tier.name} includes</DialogTitle>
        <DialogDescription>
          These are enforced while an agency is using the product, not only when it subscribes.
          Lowering a limit never removes what an agency already has — it stops them adding more.
        </DialogDescription>

        {failure ? (
          <Alert tone="destructive" title={failure.title}>
            {failure.detail}
          </Alert>
        ) : null}

        <form className="flex flex-col gap-4" onSubmit={submit}>
          <div className="flex max-h-96 flex-col gap-3 overflow-y-auto">
            {catalogue.map((item) => (
              <EntitlementField
                key={item.code}
                item={item}
                value={values[item.code] ?? item.defaultValue}
                problem={problems[item.code]}
                onChange={(value) => setValues((current) => ({ ...current, [item.code]: value }))}
              />
            ))}
          </div>

          <Textarea
            label="Why"
            rows={3}
            required
            maxLength={MAX_REASON_LENGTH}
            value={reason}
            error={reasonProblem}
            onChange={(event) => setReason(event.target.value)}
          />

          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
              Cancel
            </Button>
            <Button type="submit" loading={pending}>
              Save what it includes
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

/** One entitlement's control: a yes/no picker for a flag, a number for a limit or a fee. */
function EntitlementField({
  item,
  value,
  problem,
  onChange,
}: {
  item: EntitlementCatalogueItem;
  value: string;
  problem?: string;
  onChange: (value: string) => void;
}) {
  if (item.valueType === 'Flag') {
    return (
      <Select
        label={item.name}
        value={value === 'true' ? 'true' : 'false'}
        error={problem}
        hint={item.description}
        onChange={(event) => onChange(event.target.value)}
      >
        <option value="false">Not included</option>
        <option value="true">Included</option>
      </Select>
    );
  }

  const hint =
    item.valueType === 'Limit'
      ? `${item.description} Use -1 for unlimited.`
      : `${item.description} Basis points: 100 is 1%.`;

  return (
    <Input
      label={item.name}
      type="number"
      value={value}
      error={problem}
      hint={`${hint} Currently ${describeEntitlementValue(item.valueType, value)}.`}
      onChange={(event) => onChange(event.target.value)}
    />
  );
}

/** Publish, archive, restore or delete: the same shape, different consequences. */
export function TierActionDialog({
  open,
  onOpenChange,
  title,
  consequences,
  confirmLabel,
  destructive,
  onConfirm,
  pending,
  error,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  consequences: string;
  confirmLabel: string;
  destructive?: boolean;
  onConfirm: (reason: string) => void;
  pending: boolean;
  error: unknown;
}) {
  const [reason, setReason] = useState('');
  const [reasonProblem, setReasonProblem] = useState<string>();

  useEffect(() => {
    if (!open) {
      setReason('');
      setReasonProblem(undefined);
    }
  }, [open]);

  function submit(event: FormEvent) {
    event.preventDefault();

    const invalid = validateReason(reason);
    setReasonProblem(invalid);

    if (invalid) return;

    onConfirm(reason.trim());
  }

  const failure = error ? describeLoadError(error) : null;

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent aria-describedby={undefined}>
        <DialogTitle>{title}</DialogTitle>
        <DialogDescription>{consequences}</DialogDescription>

        {failure ? (
          <Alert tone="destructive" title={failure.title}>
            {failure.detail}
          </Alert>
        ) : null}

        <form className="flex flex-col gap-4" onSubmit={submit}>
          <Textarea
            label="Why"
            rows={3}
            required
            maxLength={MAX_REASON_LENGTH}
            value={reason}
            error={reasonProblem}
            onChange={(event) => setReason(event.target.value)}
          />

          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
              Cancel
            </Button>
            <Button
              type="submit"
              variant={destructive ? 'destructive' : 'primary'}
              loading={pending}
            >
              {confirmLabel}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

/**
 * Moving every agency on one plan to another.
 *
 * Nothing moves today. Each agency is told, and the change lands thirty days later — long enough
 * that somebody who minds can say so first.
 */
export function MigrateSubscribersDialog({
  open,
  onOpenChange,
  tier,
  targets,
  onSubmit,
  pending,
  error,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  tier: Tier;
  targets: Tier[];
  onSubmit: (request: MigrateSubscribersRequest) => void;
  pending: boolean;
  error: unknown;
}) {
  const [toTierId, setToTierId] = useState('');
  const [reason, setReason] = useState('');
  const [reasonProblem, setReasonProblem] = useState<string>();

  useEffect(() => {
    if (!open) {
      setToTierId('');
      setReason('');
      setReasonProblem(undefined);
    }
  }, [open]);

  function submit(event: FormEvent) {
    event.preventDefault();

    const invalid = validateReason(reason);
    setReasonProblem(invalid);

    if (invalid) return;

    onSubmit({ toTierId, reason: reason.trim() });
  }

  const failure = error ? describeLoadError(error) : null;

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent aria-describedby={undefined}>
        <DialogTitle>Move everyone off {tier.name}</DialogTitle>
        <DialogDescription>
          {tier.subscribers} {tier.subscribers === 1 ? 'agency is' : 'agencies are'} on this plan.
          Each is told now and moves in thirty days.
        </DialogDescription>

        {failure ? (
          <Alert tone="destructive" title={failure.title}>
            {failure.detail}
          </Alert>
        ) : null}

        <form className="flex flex-col gap-4" onSubmit={submit}>
          <Select
            label="Move them to"
            required
            value={toTierId}
            error={fieldError(error, 'toTierId')}
            hint="Only a published plan can take them."
            onChange={(event) => setToTierId(event.target.value)}
          >
            <option value="">Choose a plan</option>
            {targets.map((target) => (
              <option key={target.id} value={target.id}>
                {target.name}
              </option>
            ))}
          </Select>

          <Textarea
            label="Why"
            rows={3}
            required
            maxLength={MAX_REASON_LENGTH}
            value={reason}
            error={reasonProblem}
            hint="The agencies are shown this on their own plan screen."
            onChange={(event) => setReason(event.target.value)}
          />

          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
              Cancel
            </Button>
            <Button type="submit" loading={pending} disabled={toTierId === ''}>
              Schedule the move
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
