import { Alert, Button } from '@trips/ui';
import { formatMoney } from '@trips/utils';
import type { WalletSummary } from '../types';
import { topUpBlock } from '../top-up-rules';

/**
 * The low-balance warning.
 *
 * Placed at the top of the wallet page rather than delivered as a toast,
 * because a toast is gone by the time it matters. The threshold is the agent's
 * own, so this fires when *they* said it should, not when we guessed.
 *
 * If they cannot top up (unverified, frozen), the call to action is dropped —
 * offering a button that is about to explain why it does not work is worse than
 * offering nothing.
 */
export function LowBalanceAlert({
  summary,
  onTopUp,
}: {
  summary: WalletSummary;
  onTopUp: () => void;
}) {
  const canTopUp = topUpBlock(summary) === null;

  return (
    <Alert
      tone="warning"
      title="Your wallet is running low"
      action={
        canTopUp ? (
          <Button size="sm" onClick={onTopUp}>
            Add funds
          </Button>
        ) : undefined
      }
    >
      You have {formatMoney(summary.availableMinor, summary.currency)} available, at or below the{' '}
      {formatMoney(summary.lowBalanceThresholdMinor ?? 0, summary.currency)} you asked to be warned
      at. Bookings are declined once the wallet cannot cover them.
    </Alert>
  );
}
