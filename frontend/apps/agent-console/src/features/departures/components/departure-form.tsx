import { Plus, Trash2 } from 'lucide-react';
import { useMemo, useState, type FormEvent, type ReactNode } from 'react';
import { Button, Card, Input, SegmentedControl, Select } from '@trips/ui';
import { formatMoneyShort } from '@trips/utils';
import { cx } from '../../search/class-names';
import {
  buildDepartureRequest,
  emptyInstallment,
  emptyTier,
  formatDay,
  lowestPriceMinor,
  paymentSchedule,
  plusDays,
  tierMins,
  todayInLagos,
  validateDeparture,
  type DepartureDraft,
  type InstallmentDraft,
  type Problems,
  type TierDraft,
} from '../departure-rules';
import type { DepartureRequest, DepositType, DueBasis } from '../types';

const DEPOSIT_TYPES: ReadonlyArray<{ value: DepositType; label: string }> = [
  { value: 'Percent', label: 'Percentage' },
  { value: 'Fixed', label: 'Fixed amount' },
  { value: 'None', label: 'No deposit' },
];

/**
 * Build plan F6 — one departure: when, how many, the price per traveller by
 * party size, and how it is paid. The payment schedule a traveller booking
 * today would get is shown as the terms are typed, so the agent sees what the
 * customer will see.
 */
export function DepartureForm({
  currency,
  initial,
  saving,
  submitLabel,
  onSubmit,
}: {
  currency: string;
  initial: DepartureDraft;
  saving: boolean;
  submitLabel: string;
  onSubmit: (request: DepartureRequest) => void;
}) {
  const [draft, setDraft] = useState(initial);
  const [problems, setProblems] = useState<Problems>({});
  const today = todayInLagos();
  const mins = tierMins(draft.tiers);
  const money = (amountMinor: number) => formatMoneyShort(amountMinor, currency);

  const built = useMemo(() => buildDepartureRequest(draft), [draft]);
  const lowest = built.ok ? lowestPriceMinor(built.request.priceTiers) : null;
  const schedule =
    built.ok && lowest !== null && /^\d{4}-\d{2}-\d{2}$/.test(built.request.departureDate)
      ? paymentSchedule(built.request, today, lowest)
      : null;
  const shareTotal = draft.installments.reduce(
    (sum, item) => sum + (Number.parseFloat(item.share) || 0),
    0,
  );

  function update(patch: Partial<DepartureDraft>) {
    setDraft((current) => ({ ...current, ...patch }));
  }

  function updateTier(index: number, patch: Partial<TierDraft>) {
    update({ tiers: draft.tiers.map((tier, i) => (i === index ? { ...tier, ...patch } : tier)) });
  }

  function updateInstallment(index: number, patch: Partial<InstallmentDraft>) {
    update({
      installments: draft.installments.map((item, i) =>
        i === index ? { ...item, ...patch } : item,
      ),
    });
  }

  function submit(event: FormEvent) {
    event.preventDefault();
    const result = buildDepartureRequest(draft);
    if (!result.ok) {
      setProblems(result.errors);
      return;
    }
    const found = validateDeparture(result.request, today);
    setProblems(found);
    if (Object.keys(found).length === 0) onSubmit(result.request);
  }

  return (
    <form onSubmit={submit} noValidate aria-label="Departure" className="flex flex-col gap-6">
      <Section title="When, and how many">
        <div className="grid items-start gap-3 sm:grid-cols-2">
          <Input
            type="date"
            label="Departs"
            value={draft.departureDate}
            onChange={(event) => update({ departureDate: event.target.value })}
            error={problems['departureDate']}
          />
          <Input
            label="Bookings close (days before)"
            inputMode="numeric"
            hint={
              /^\d+$/.test(draft.cutoffDaysBefore) && draft.departureDate
                ? `On ${formatDay(plusDays(draft.departureDate, -Number(draft.cutoffDaysBefore)))}`
                : undefined
            }
            value={draft.cutoffDaysBefore}
            onChange={(event) => update({ cutoffDaysBefore: event.target.value })}
            error={problems['cutoffDaysBefore']}
          />
          <Input
            label="Seats"
            inputMode="numeric"
            value={draft.capacityTotal}
            onChange={(event) => update({ capacityTotal: event.target.value })}
            error={problems['capacityTotal']}
          />
          {draft.isGroupDeparture ? (
            <Input
              label="Travellers needed to run"
              inputMode="numeric"
              value={draft.minPax}
              onChange={(event) => update({ minPax: event.target.value })}
              error={problems['minPax']}
            />
          ) : null}
        </div>
        <label className="flex items-start gap-2 text-sm text-foreground">
          <input
            type="checkbox"
            className="mt-0.5 h-4 w-4 accent-primary"
            checked={draft.isGroupDeparture}
            onChange={(event) => update({ isGroupDeparture: event.target.checked })}
          />
          <span>
            A group departure: it only runs once enough travellers have paid, and customers are told
            so until it does.
          </span>
        </label>
      </Section>

      <Section
        title="Price per traveller"
        description="Larger parties can pay less each. Each size starts where the one before it ends."
      >
        {problems['priceTiers'] ? (
          <p className="text-sm text-destructive">{problems['priceTiers']}</p>
        ) : null}
        <ol aria-label="Party sizes" className="flex flex-col gap-3">
          {draft.tiers.map((tier, index) => {
            const last = index === draft.tiers.length - 1;
            const from = mins[index];

            return (
              <li
                key={tier.key}
                className="flex flex-wrap items-end gap-3 rounded-lg border border-border p-3"
              >
                <p className="w-24 pb-2.5 text-sm font-medium text-foreground">
                  {Number.isNaN(from) ? 'From —' : `From ${from}`}
                </p>
                <div className="w-32">
                  <Input
                    label="Up to"
                    inputMode="numeric"
                    placeholder={last ? 'and up' : undefined}
                    value={tier.maxPax}
                    onChange={(event) => updateTier(index, { maxPax: event.target.value })}
                    error={problems[`priceTiers.${index}.maxPax`]}
                  />
                </div>
                <div className="min-w-0 flex-1 basis-40">
                  <Input
                    label={`Price each (${currency})`}
                    inputMode="decimal"
                    value={tier.price}
                    onChange={(event) => updateTier(index, { price: event.target.value })}
                    error={problems[`priceTiers.${index}.price`]}
                  />
                </div>
                {draft.tiers.length > 1 ? (
                  <Button
                    type="button"
                    variant="ghost"
                    size="icon"
                    aria-label={`Remove party size ${index + 1}`}
                    onClick={() => update({ tiers: draft.tiers.filter((_, i) => i !== index) })}
                  >
                    <Trash2 aria-hidden="true" className="h-4 w-4" />
                  </Button>
                ) : null}
              </li>
            );
          })}
        </ol>
        <div>
          <Button
            type="button"
            variant="outline"
            size="sm"
            onClick={() => update({ tiers: [...draft.tiers, emptyTier()] })}
          >
            <Plus aria-hidden="true" className="h-4 w-4" />
            Add a party size
          </Button>
        </div>
      </Section>

      <Section title="Deposit and balance">
        <SegmentedControl
          label="Deposit"
          options={DEPOSIT_TYPES}
          value={draft.depositType}
          onChange={(depositType) => update({ depositType })}
        />
        {draft.depositType === 'Percent' ? (
          <div className="w-48">
            <Input
              label="Deposit (% of the price)"
              inputMode="decimal"
              value={draft.depositPercent}
              onChange={(event) => update({ depositPercent: event.target.value })}
              error={problems['deposit']}
            />
          </div>
        ) : draft.depositType === 'Fixed' ? (
          <div className="w-56">
            <Input
              label={`Deposit per traveller (${currency})`}
              inputMode="decimal"
              value={draft.depositAmount}
              onChange={(event) => update({ depositAmount: event.target.value })}
              error={problems['deposit']}
            />
          </div>
        ) : null}

        <fieldset className="flex flex-col gap-3">
          <legend className="mb-1 text-sm font-medium text-foreground">The balance</legend>
          {draft.installments.length === 0 ? (
            <p className="text-sm text-muted-foreground">
              Paid in one go when bookings close. Add payments to spread it out.
            </p>
          ) : (
            <ol aria-label="Balance payments" className="flex flex-col gap-3">
              {draft.installments.map((item, index) => (
                <li
                  key={item.key}
                  className="flex flex-wrap items-end gap-3 rounded-lg border border-border p-3"
                >
                  <div className="w-40">
                    <Input
                      label={`Payment ${index + 1} (% of balance)`}
                      inputMode="decimal"
                      value={item.share}
                      onChange={(event) => updateInstallment(index, { share: event.target.value })}
                      error={problems[`installments.${index}.share`]}
                    />
                  </div>
                  <div className="w-24">
                    <Input
                      label="Days"
                      inputMode="numeric"
                      value={item.offsetDays}
                      onChange={(event) =>
                        updateInstallment(index, { offsetDays: event.target.value })
                      }
                      error={problems[`installments.${index}.offset`]}
                    />
                  </div>
                  <div className="w-48">
                    <Select
                      label="Counted"
                      value={item.dueBasis}
                      onChange={(event) =>
                        updateInstallment(index, { dueBasis: event.target.value as DueBasis })
                      }
                    >
                      <option value="BeforeDeparture">before departure</option>
                      <option value="FromBooking">after booking</option>
                    </Select>
                  </div>
                  <Button
                    type="button"
                    variant="ghost"
                    size="icon"
                    aria-label={`Remove payment ${index + 1}`}
                    onClick={() =>
                      update({ installments: draft.installments.filter((_, i) => i !== index) })
                    }
                  >
                    <Trash2 aria-hidden="true" className="h-4 w-4" />
                  </Button>
                </li>
              ))}
            </ol>
          )}
          {draft.installments.length > 0 ? (
            <p
              className={cx(
                'text-sm',
                problems['installments'] ? 'text-destructive' : 'text-muted-foreground',
              )}
            >
              {problems['installments'] ??
                `These payments cover ${Math.round(shareTotal * 100) / 100}% of the balance.`}
            </p>
          ) : null}
          <div>
            <Button
              type="button"
              variant="outline"
              size="sm"
              onClick={() => update({ installments: [...draft.installments, emptyInstallment()] })}
            >
              <Plus aria-hidden="true" className="h-4 w-4" />
              Add a payment
            </Button>
          </div>
        </fieldset>

        {schedule && lowest !== null ? (
          <div className="rounded-lg bg-muted p-4">
            <p className="text-sm font-medium text-foreground">
              One traveller booking today at {money(lowest)} pays:
            </p>
            <ul aria-label="Payment schedule" className="mt-2 flex flex-col gap-1 text-sm">
              {schedule.map((line) => (
                <li key={line.label} className="flex flex-wrap justify-between gap-2">
                  <span className="text-foreground">
                    {line.label} ·{' '}
                    <span className="text-muted-foreground">
                      {line.dueNow ? 'when they book' : formatDay(line.dueDate)}
                    </span>
                  </span>
                  <span className="tabular-nums text-foreground">{money(line.amountMinor)}</span>
                </li>
              ))}
            </ul>
          </div>
        ) : null}
      </Section>

      <div className="flex justify-end">
        <Button type="submit" size="lg" loading={saving}>
          {submitLabel}
        </Button>
      </div>
    </form>
  );
}

function Section({
  title,
  description,
  children,
}: {
  title: string;
  description?: string;
  children: ReactNode;
}) {
  return (
    <Card className="flex flex-col gap-4 p-5">
      <div>
        <h2 className="text-base font-semibold text-foreground">{title}</h2>
        {description ? <p className="mt-1 text-sm text-muted-foreground">{description}</p> : null}
      </div>
      {children}
    </Card>
  );
}
