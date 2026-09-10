import { useState, type FormEvent } from 'react';
import {
  Alert,
  Button,
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
  Input,
} from '@trips/ui';
import { formatMoney } from '@trips/utils';
import type { WalletSummary } from '../types';
import { QUICK_TOPUP_AMOUNTS_MINOR, parseTopUpAmount, topUpBlock } from '../top-up-rules';
import { rememberPendingTopUp } from '../pending-top-up';
import { useStartTopUp } from '../wallet-queries';

/**
 * Add funds.
 *
 * The flow, and why it is shaped this way:
 *
 *   1. the agent names an amount;
 *   2. we ask the server to register the intent and hand back a gateway URL;
 *   3. we write down the reference, then hand the whole tab to Paystack.
 *
 * Step 2 exists so the amount is fixed server-side before the agent ever
 * reaches the gateway. Nothing about the money is decided in this file.
 */
export function TopUpPanel({ summary }: { summary: WalletSummary }) {
  const [amount, setAmount] = useState('');
  const [error, setError] = useState<string | null>(null);
  const startTopUp = useStartTopUp();

  const block = topUpBlock(summary);

  if (block) {
    return (
      <Card>
        <CardHeader>
          <CardTitle>Add funds</CardTitle>
        </CardHeader>
        <CardContent>
          <Alert tone="warning" title={block.title}>
            {block.detail}
          </Alert>
        </CardContent>
      </Card>
    );
  }

  function submit(event: FormEvent) {
    event.preventDefault();

    const parsed = parseTopUpAmount(amount, summary.currency);
    if (!parsed.ok) {
      setError(parsed.error);
      return;
    }
    setError(null);

    startTopUp.mutate(parsed.amountMinor, {
      onSuccess: (intent) => {
        // Written down BEFORE we navigate away. If the agent never comes back
        // through the return URL, this breadcrumb is the only way the wallet
        // page can tell them what became of the payment.
        rememberPendingTopUp({
          reference: intent.reference,
          amountMinor: intent.amountMinor,
          currency: summary.currency,
          startedAt: new Date().toISOString(),
        });

        // A full-page navigation, not a router push: the gateway is somebody
        // else's origin, and the card form must run there, never here.
        window.location.assign(intent.authorizationUrl);
      },
    });
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Add funds</CardTitle>
        <CardDescription>
          You will be taken to Paystack to pay by card or bank transfer. Your balance updates once
          the payment is confirmed.
        </CardDescription>
      </CardHeader>

      <CardContent>
        <form onSubmit={submit} className="flex flex-col gap-4">
          <div className="flex flex-wrap gap-2">
            {QUICK_TOPUP_AMOUNTS_MINOR.map((quick) => (
              <Button
                key={quick}
                type="button"
                variant="outline"
                size="sm"
                onClick={() => {
                  // Whole naira: the input takes a major-unit figure, which is
                  // what the agent thinks in. Conversion to kobo happens once,
                  // in parseTopUpAmount, so there is a single place to get wrong.
                  setAmount(String(quick / 100));
                  setError(null);
                }}
              >
                {formatMoney(quick, summary.currency)}
              </Button>
            ))}
          </div>

          <Input
            label={`Amount (${summary.currency})`}
            inputMode="decimal"
            autoComplete="off"
            placeholder="50000"
            value={amount}
            onChange={(event) => {
              setAmount(event.target.value);
              setError(null);
            }}
            error={error ?? undefined}
            hint="Minimum 1,000. Maximum 5,000,000 per top-up."
          />

          {startTopUp.isError ? (
            <Alert tone="destructive" title="We could not start that top-up">
              Nothing has been charged. Please try again in a moment, and contact support if it
              keeps happening.
            </Alert>
          ) : null}

          <Button type="submit" loading={startTopUp.isPending || startTopUp.isSuccess}>
            {/* isSuccess keeps it disabled through the redirect, so a second
                click during the hand-off cannot open two gateway sessions. */}
            Continue to payment
          </Button>
        </form>
      </CardContent>
    </Card>
  );
}
