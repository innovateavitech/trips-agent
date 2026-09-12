import type { Metadata } from 'next';
import Link from 'next/link';
import { formatMoney, int64 } from '@trips/utils';
import { getCart } from '../../lib/cart';
import { removeItem } from './actions';

/**
 * What the traveller has chosen, before they buy it.
 *
 * Every figure here is the sell price they will pay. A cart price is indicative until checkout
 * re-prices it — which is said on the page, because a price that moves at the last step with no
 * warning is the thing travellers most reasonably resent.
 */

export const metadata: Metadata = {
  title: 'Your cart',
  // A cart is one browser's, and every cart is a different page. Nothing for a search engine here.
  robots: { index: false, follow: false },
};

export default async function CartPage() {
  const cart = await getCart();
  const items = cart?.items ?? [];

  return (
    <div className="mx-auto max-w-3xl px-4 py-12 sm:px-6">
      <h1 className="text-3xl font-semibold tracking-tight text-foreground">Your cart</h1>

      {items.length === 0 ? (
        <div className="mt-8 rounded-lg border border-border bg-card p-8 text-center">
          <p className="text-sm text-muted-foreground">There is nothing in your cart yet.</p>
          <Link
            href="/tours"
            className="mt-6 inline-flex h-11 items-center justify-center rounded-md bg-primary px-6 text-sm font-medium text-primary-foreground transition-colors hover:bg-primary-hover focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2"
          >
            Browse our trips
          </Link>
        </div>
      ) : (
        <>
          <ul className="mt-8 divide-y divide-border border-y border-border">
            {items.map((item) => (
              <li key={item.id} className="flex flex-wrap items-start gap-x-6 gap-y-3 py-5">
                <div className="min-w-0 flex-1">
                  <p className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
                    {readableType(item.itemType)}
                  </p>
                  <p className="mt-1 text-base font-medium text-foreground">{item.title}</p>
                  <p className="mt-1 text-sm text-muted-foreground">{travellers(item)}</p>
                </div>

                <p className="text-base font-semibold text-foreground">
                  {formatMoney(int64(item.priceMinor), item.currency)}
                </p>

                <form action={removeItem}>
                  <input type="hidden" name="cartItemId" value={item.id} />
                  <button
                    type="submit"
                    className="text-sm font-medium text-muted-foreground underline underline-offset-4 transition-colors hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2"
                  >
                    Remove
                  </button>
                </form>
              </li>
            ))}
          </ul>

          <div className="mt-6 flex items-baseline justify-between">
            <span className="text-sm text-muted-foreground">Total</span>
            <span className="text-2xl font-semibold text-foreground">
              {formatMoney(int64(cart!.totalMinor), cart!.currency)}
            </span>
          </div>

          <p className="mt-2 text-xs text-muted-foreground">
            Prices are checked again when you check out, and we will tell you before you pay if
            anything has changed.
          </p>

          <Link
            href="/checkout"
            className="mt-8 inline-flex h-11 w-full items-center justify-center rounded-md bg-primary px-6 text-sm font-medium text-primary-foreground transition-colors hover:bg-primary-hover focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 sm:w-auto"
          >
            Check out
          </Link>
        </>
      )}
    </div>
  );
}

/** "2 adults, 1 child" — how a person says it, not how the API stores it. */
function travellers(item: {
  adults: number | string;
  children: number | string;
  infants: number | string;
}): string {
  const parts = [
    plural(int64(item.adults), 'adult', 'adults'),
    plural(int64(item.children), 'child', 'children'),
    plural(int64(item.infants), 'infant', 'infants'),
  ].filter((part) => part !== null);

  return parts.join(', ');
}

function plural(count: number, one: string, many: string): string | null {
  return count > 0 ? `${count} ${count === 1 ? one : many}` : null;
}

/** The item type in the traveller's words rather than the schema's. */
function readableType(itemType: string): string {
  switch (itemType) {
    case 'GroupDeparture':
      return 'Group departure';
    case 'Visa':
      return 'Visa service';
    default:
      return itemType;
  }
}
