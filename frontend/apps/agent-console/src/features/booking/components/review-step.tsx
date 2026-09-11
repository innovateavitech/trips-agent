import { useEffect, useState, type ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { Alert, Button, Card, CardHeader, CardTitle, ErrorState, buttonVariants } from '@trips/ui';
import { formatMoneyShort } from '@trips/utils';
import { describeError } from '../../../api/errors';
import { useCurrentUser } from '../../../auth/auth-provider';
import { cx } from '../../search/class-names';
import { canViewMargin } from '../../search/search-rules';
import { useCountdown } from '../../search/use-countdown';
import { useWalletSummary, type WalletSummary } from '../../wallet';
import { usePlaceBooking } from '../booking-api';
import { canPayFromWallet, confirmedMargin, priceChanged, slotLabels } from '../booking-rules';
import type { BookingDraft, PaymentMethod, PriceConfirmation, TravellerDetails } from '../types';

/** The one place the wallet summary's balance is read, so its shape is only assumed once. */
function walletBalanceMinor(summary: WalletSummary | undefined): number | undefined {
  return summary?.balanceMinor;
}

/**
 * #53, step two — everything the customer is agreeing to, before any money moves:
 * who, the fare rules, the full price (the margin only with `margin.view`), and
 * how it is paid.
 *
 * One idempotency key per visit to this step, reused on every retry, so a double
 * press or a retry after a lost answer is the same booking — never a second charge.
 */
export function ReviewStep({
  draft,
  travellers,
  confirmation,
  onBack,
  onPlaced,
}: {
  draft: BookingDraft;
  travellers: TravellerDetails[];
  confirmation: PriceConfirmation;
  onBack: () => void;
  onPlaced: (reference: string) => void;
}) {
  const user = useCurrentUser();
  const wallet = useWalletSummary();
  const place = usePlaceBooking();
  const [method, setMethod] = useState<PaymentMethod>('wallet');
  const [idempotencyKey] = useState(() => crypto.randomUUID());
  const secondsLeft = useCountdown(confirmation.ticketTimeLimit);

  const balance = walletBalanceMinor(wallet.data);
  const affordable = canPayFromWallet(balance, confirmation.sellMinor);
  const expired = secondsLeft === 0;
  const margin = canViewMargin(user.roles)
    ? confirmedMargin(draft.offer.price.margin, confirmation)
    : null;
  const labels = slotLabels(travellers.map((traveller) => traveller.type));
  const money = (amountMinor: number) => formatMoneyShort(amountMinor, confirmation.currency);
  const walletBlocked = method === 'wallet' && affordable === false;

  // Not enough in the wallet: offer the card rather than leave the agent on a dead choice.
  useEffect(() => {
    if (affordable === false) setMethod('card');
  }, [affordable]);

  function pay() {
    if (place.isPending || expired || walletBlocked) return;
    place.mutate(
      { draft, travellers, payment: method, confirmation, idempotencyKey },
      { onSuccess: (placed) => onPlaced(placed.reference) },
    );
  }

  return (
    <div className="flex flex-col gap-6">
      <Card>
        <CardHeader className="flex-row items-center justify-between">
          <CardTitle>Travellers</CardTitle>
          <Button
            variant="link"
            size="sm"
            className="px-0"
            onClick={onBack}
            disabled={place.isPending}
          >
            Change
          </Button>
        </CardHeader>
        <ul className="divide-y divide-border border-t border-border">
          {travellers.map((traveller, index) => (
            <li key={index} className="flex flex-wrap justify-between gap-2 px-5 py-3 text-sm">
              <span className="text-foreground">
                {[traveller.title, traveller.firstName, traveller.lastName]
                  .filter(Boolean)
                  .join(' ')}
              </span>
              <span className="text-muted-foreground">{labels[index]}</span>
            </li>
          ))}
        </ul>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Fare rules</CardTitle>
        </CardHeader>
        <dl className="grid gap-4 border-t border-border px-5 py-4 text-sm sm:grid-cols-2">
          {draft.product === 'flight' ? (
            <>
              <Term label="Refunds">
                {draft.offer.terms.refundable ? 'Refundable' : 'Non-refundable'}.{' '}
                {draft.offer.terms.cancellation}
              </Term>
              <Term label="Changes">{draft.offer.terms.changes}</Term>
              <Term label="Checked baggage">{draft.offer.terms.checkedBaggage}</Term>
              <Term label="Cabin baggage">{draft.offer.terms.cabinBaggage}</Term>
            </>
          ) : (
            <>
              <Term label="Cancellation">{draft.offer.terms.cancellation}</Term>
              <Term label="Luggage">{draft.offer.terms.luggage}</Term>
            </>
          )}
        </dl>
      </Card>

      <Card className="flex flex-col gap-3 p-5">
        <p className="text-xs text-muted-foreground">Your customer pays</p>
        <p className="text-3xl font-semibold tabular-nums text-foreground">
          {money(confirmation.sellMinor)}
        </p>
        {priceChanged(confirmation) ? (
          <p className="text-xs text-muted-foreground">
            The supplier&rsquo;s new price, which your customer agreed to. The search showed{' '}
            {money(confirmation.searchedSellMinor)}.
          </p>
        ) : null}
        {margin ? (
          <dl className="flex flex-col gap-1 border-t border-border pt-3 text-sm">
            <div className="flex justify-between text-muted-foreground">
              <dt>Net</dt>
              <dd className="tabular-nums">{money(margin.netMinor)}</dd>
            </div>
            <div className="flex justify-between font-medium text-success-subtle-foreground">
              <dt>Your margin</dt>
              <dd className="tabular-nums">{money(margin.markupMinor)}</dd>
            </div>
          </dl>
        ) : null}
      </Card>

      <fieldset className="flex flex-col gap-3">
        <legend className="mb-2 text-sm font-medium text-foreground">
          How is this being paid?
        </legend>
        <PaymentOption
          value="wallet"
          selected={method}
          onSelect={setMethod}
          disabled={affordable === false}
          title="Your wallet"
          detail={
            balance === undefined
              ? 'Checking your balance…'
              : affordable
                ? `Balance ${money(balance)} — ${money(balance - confirmation.sellMinor)} left after this booking.`
                : `Balance ${money(balance)} is not enough. Top up, or take the customer's card.`
          }
        />
        <PaymentOption
          value="card"
          selected={method}
          onSelect={setMethod}
          title="Customer's card"
          detail="They pay on the payment provider's secure page. The card never touches this console."
        />
      </fieldset>

      {expired ? (
        <Alert
          tone="destructive"
          title="The time limit has passed"
          action={
            <Link
              to={draft.product === 'flight' ? '/search/flights' : '/search/buses'}
              className={buttonVariants({ size: 'sm', variant: 'outline' })}
            >
              Search again
            </Link>
          }
        >
          The supplier no longer holds this fare. Nothing has been charged.
        </Alert>
      ) : null}

      {place.isError ? (
        <ErrorState
          title="We could not place the booking"
          detail={`${describeError(place.error).detail} Nothing has been charged, and trying again cannot charge twice.`}
          onRetry={pay}
          retrying={place.isPending}
        />
      ) : null}

      <div className="flex flex-wrap justify-between gap-3">
        <Button variant="outline" onClick={onBack} disabled={place.isPending}>
          Back
        </Button>
        <Button
          size="lg"
          onClick={pay}
          loading={place.isPending}
          disabled={expired || walletBlocked}
        >
          Pay {money(confirmation.sellMinor)} {method === 'wallet' ? 'from wallet' : 'by card'}
        </Button>
      </div>
    </div>
  );
}

function Term({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div>
      <dt className="text-xs font-medium text-muted-foreground">{label}</dt>
      <dd className="mt-0.5 text-foreground">{children}</dd>
    </div>
  );
}

function PaymentOption({
  value,
  selected,
  onSelect,
  disabled = false,
  title,
  detail,
}: {
  value: PaymentMethod;
  selected: PaymentMethod;
  onSelect: (method: PaymentMethod) => void;
  disabled?: boolean;
  title: string;
  detail: string;
}) {
  return (
    <label
      className={cx(
        'flex cursor-pointer items-start gap-3 rounded-lg border p-4',
        selected === value ? 'border-primary bg-primary-subtle' : 'border-border',
        disabled && 'cursor-not-allowed opacity-60',
      )}
    >
      <input
        type="radio"
        name="payment-method"
        value={value}
        checked={selected === value}
        disabled={disabled}
        onChange={() => onSelect(value)}
        className="mt-1 h-4 w-4 accent-primary"
      />
      <span className="flex flex-col gap-0.5">
        <span className="text-sm font-medium text-foreground">{title}</span>
        <span className="text-xs text-muted-foreground">{detail}</span>
      </span>
    </label>
  );
}
