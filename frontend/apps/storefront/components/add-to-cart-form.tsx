'use client';

import { useActionState } from 'react';
import { addItem, type CartActionState } from '../app/cart/actions';

/**
 * One button that puts a trip, a departure or a fare in the cart.
 *
 * A form and a server action rather than a click handler, so it works before any JavaScript has
 * loaded — which on the connections this audience has is most of the time somebody is on the page.
 * The party size travels as hidden fields, because it is what the price depends on.
 */
export function AddToCartForm({
  productId,
  departureId,
  offerId,
  party,
  label = 'Add to cart',
}: {
  productId?: string;
  departureId?: string;
  offerId?: string;
  party: { adults: number; children: number; infants: number };
  label?: string;
}) {
  const [state, action, pending] = useActionState<CartActionState, FormData>(addItem, {
    status: 'idle',
  });

  return (
    <form action={action}>
      {productId ? <input type="hidden" name="productId" value={productId} /> : null}
      {departureId ? <input type="hidden" name="departureId" value={departureId} /> : null}
      {offerId ? <input type="hidden" name="offerId" value={offerId} /> : null}

      <input type="hidden" name="adults" value={party.adults} />
      <input type="hidden" name="children" value={party.children} />
      <input type="hidden" name="infants" value={party.infants} />

      {state.status === 'failed' && state.message ? (
        <p
          role="alert"
          className="mb-3 rounded-md bg-destructive-subtle px-3 py-2 text-sm text-destructive-subtle-foreground"
        >
          {state.message}
        </p>
      ) : null}

      <button
        type="submit"
        disabled={pending}
        className="inline-flex h-11 items-center justify-center rounded-md bg-primary px-6 text-sm font-medium text-primary-foreground transition-colors hover:bg-primary-hover disabled:opacity-60 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2"
      >
        {pending ? 'Adding…' : label}
      </button>
    </form>
  );
}
