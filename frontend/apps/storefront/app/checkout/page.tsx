import type { Metadata } from 'next';
import Link from 'next/link';
import { formatMoney, int64 } from '@trips/utils';
import { CheckoutForm } from '../../components/checkout-form';
import { getCart } from '../../lib/cart';

/**
 * Guest checkout: who is buying, who is travelling, and on to the payment page.
 *
 * There is no account to make and no password to choose (build plan decision 21). Once they have
 * paid, a link to manage the booking is emailed to them, and that link is how they get back in.
 */

export const metadata: Metadata = {
  title: 'Checkout',
  robots: { index: false, follow: false },
};

export default async function CheckoutPage() {
  const cart = await getCart();

  if (!cart || cart.items.length === 0) {
    return (
      <div className="mx-auto max-w-3xl px-4 py-12 sm:px-6">
        <h1 className="text-3xl font-semibold tracking-tight text-foreground">Checkout</h1>
        <p className="mt-4 text-sm text-muted-foreground">
          There is nothing in your cart to check out.
        </p>
        <Link
          href="/tours"
          className="mt-6 inline-flex h-11 items-center justify-center rounded-md bg-primary px-6 text-sm font-medium text-primary-foreground transition-colors hover:bg-primary-hover focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2"
        >
          Browse our trips
        </Link>
      </div>
    );
  }

  return (
    <div className="mx-auto max-w-3xl px-4 py-12 sm:px-6">
      <h1 className="text-3xl font-semibold tracking-tight text-foreground">Checkout</h1>

      <section className="mt-8 rounded-lg border border-border bg-card p-6">
        <h2 className="text-sm font-medium uppercase tracking-wide text-muted-foreground">
          What you are booking
        </h2>

        <ul className="mt-4 divide-y divide-border">
          {cart.items.map((item) => (
            <li key={item.id} className="flex items-baseline justify-between gap-4 py-3">
              <span className="text-sm text-foreground">{item.title}</span>
              <span className="shrink-0 text-sm font-medium text-foreground">
                {formatMoney(int64(item.priceMinor), item.currency)}
              </span>
            </li>
          ))}
        </ul>
      </section>

      <CheckoutForm cart={cart} />
    </div>
  );
}
