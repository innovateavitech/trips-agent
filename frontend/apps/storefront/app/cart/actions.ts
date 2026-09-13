'use server';

import { revalidatePath } from 'next/cache';
import { redirect } from 'next/navigation';
import { addToCart, removeFromCart } from '../../lib/cart';

/**
 * Putting things in the cart and taking them out (build plan F5).
 *
 * Server actions rather than a `fetch` from the page, for the same two reasons the enquiry form is
 * one. Every button works with JavaScript switched off, which matters on the connections this
 * audience actually has. And the API is called from our server, so its address never reaches a
 * browser and the hostname it is told is the one the request arrived on — not one a caller chose.
 */

export interface CartActionState {
  status: 'idle' | 'added' | 'failed';
  message?: string;
}

/** Adds a trip, a departure or a fare to the cart, then shows what is in it. */
export async function addItem(
  _previous: CartActionState,
  form: FormData,
): Promise<CartActionState> {
  const result = await addToCart(
    {
      productId: text(form, 'productId'),
      departureId: text(form, 'departureId'),
      offerId: text(form, 'offerId'),
    },
    {
      adults: count(form, 'adults', 1),
      children: count(form, 'children', 0),
      infants: count(form, 'infants', 0),
    },
  );

  if (!result.ok) {
    // The API's own words. It knows whether the tour sold out, the party is too large, or the cart
    // is full; repeating that is more use to a traveller than "something went wrong".
    return {
      status: 'failed',
      message:
        firstProblem(result.problem?.errors) ??
        result.problem?.detail ??
        result.problem?.title ??
        'We could not add that to your cart just now. Please try again.',
    };
  }

  revalidatePath('/cart');
  redirect('/cart');
}

/** Takes one line out of the cart. */
export async function removeItem(form: FormData): Promise<void> {
  const cartItemId = text(form, 'cartItemId');

  if (cartItemId) {
    await removeFromCart(cartItemId);
  }

  revalidatePath('/cart');
}

function text(form: FormData, field: string): string | undefined {
  const value = form.get(field);
  const trimmed = typeof value === 'string' ? value.trim() : '';

  return trimmed.length > 0 ? trimmed : undefined;
}

function count(form: FormData, field: string, fallback: number): number {
  const parsed = Number(form.get(field));

  return Number.isSafeInteger(parsed) && parsed >= 0 ? parsed : fallback;
}

/** The first field-level message, which is the one to put in front of the traveller. */
function firstProblem(errors: Record<string, string[]> | undefined): string | undefined {
  return errors ? Object.values(errors).flat()[0] : undefined;
}
