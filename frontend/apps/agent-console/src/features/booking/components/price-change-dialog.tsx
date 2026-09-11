import {
  Button,
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogTitle,
} from '@trips/ui';
import { formatMoneyShort } from '@trips/utils';
import { cx } from '../../search/class-names';
import type { PriceConfirmation } from '../types';

/**
 * #53 — the supplier confirmed a different price from the one in the search.
 * Nothing goes on until the agent has asked the customer and said yes: a price
 * already given never moves silently (CLAUDE.md rule 5).
 *
 * Closing it any way — Escape, the backdrop, the button — declines.
 */
export function PriceChangeDialog({
  confirmation,
  onAccept,
  onDecline,
}: {
  confirmation: PriceConfirmation | null;
  onAccept: () => void;
  onDecline: () => void;
}) {
  const difference = confirmation ? confirmation.sellMinor - confirmation.searchedSellMinor : 0;
  const money = (amountMinor: number) =>
    formatMoneyShort(amountMinor, confirmation?.currency ?? 'NGN');

  return (
    <Dialog open={confirmation !== null} onOpenChange={(open) => (open ? undefined : onDecline())}>
      {confirmation ? (
        <DialogContent>
          <DialogTitle>{difference > 0 ? 'The price went up' : 'The price went down'}</DialogTitle>
          <DialogDescription>
            The supplier confirmed a different price from the one in the search. Ask your customer
            before going on — nothing has been booked or charged.
          </DialogDescription>

          <dl className="grid grid-cols-2 gap-4">
            <div>
              <dt className="text-xs text-muted-foreground">Was</dt>
              <dd className="text-lg tabular-nums text-muted-foreground">
                {money(confirmation.searchedSellMinor)}
              </dd>
            </div>
            <div>
              <dt className="text-xs text-muted-foreground">Now</dt>
              <dd className="text-2xl font-semibold tabular-nums text-foreground">
                {money(confirmation.sellMinor)}
              </dd>
            </div>
          </dl>

          <p
            className={cx(
              'text-sm font-medium',
              difference > 0 ? 'text-destructive' : 'text-success-subtle-foreground',
            )}
          >
            {difference > 0 ? `${money(difference)} more` : `${money(-difference)} less`}
          </p>

          <DialogFooter>
            <Button variant="outline" onClick={onDecline}>
              Don&rsquo;t accept
            </Button>
            <Button onClick={onAccept}>Customer agrees — continue</Button>
          </DialogFooter>
        </DialogContent>
      ) : null}
    </Dialog>
  );
}
