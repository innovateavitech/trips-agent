import { Link } from 'react-router-dom';
import { Alert, buttonVariants } from '@trips/ui';
import { formatMoney } from '@trips/utils';
import { readPendingTopUp } from '../pending-top-up';

/**
 * Catches the third outcome of a hosted-gateway payment: the agent never came
 * back through the return URL at all.
 *
 * They closed the tab, hit Back, or reopened the console from a bookmark. From
 * our side that is silence, and silence next to an unchanged balance reads as
 * "my money vanished". So if a breadcrumb from `pending-top-up` is still around
 * when the wallet page loads, we say so and offer to go and check.
 *
 * Read once on render rather than watched: this is a page-load question, and
 * the breadcrumb only changes on a full navigation anyway.
 */
export function UnfinishedTopUpAlert() {
  const pending = readPendingTopUp();
  if (!pending) return null;

  return (
    <Alert
      tone="info"
      title="You started a top-up that we have not confirmed yet"
      action={
        // A link styled as a button, not a Button wrapping a link. Nesting an
        // anchor inside a button is invalid HTML and gives screen readers two
        // controls where the agent can see one.
        <Link
          to={`/wallet/top-up/return?reference=${encodeURIComponent(pending.reference)}`}
          className={buttonVariants({ variant: 'outline', size: 'sm' })}
        >
          Check what happened
        </Link>
      }
    >
      A top-up of {formatMoney(pending.amountMinor, pending.currency)} was started but never
      confirmed. If you did not finish paying, nothing has been charged and your balance is
      unchanged.
    </Alert>
  );
}
