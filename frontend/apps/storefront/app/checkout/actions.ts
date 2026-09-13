'use server';

import { redirect } from 'next/navigation';
import { int64 } from '@trips/utils';
import { commerce, type CheckoutStarted } from '../../lib/api';
import { cartToken, forgetCart, getCart } from '../../lib/cart';

/**
 * Starting a checkout, and sending the traveller to the gateway to pay (build plan F5).
 *
 * **No card detail passes through here, or anywhere else on this site.** The traveller is redirected
 * to the gateway's own hosted page, types their card there, and comes back. That is decision 18, and
 * it is what keeps the platform in PCI SAQ-A — a form on this page that took a card number would
 * undo it in one commit.
 */

export interface CheckoutState {
  status: 'idle' | 'failed';
  message?: string;
  /** What they typed, so a refused form comes back filled in rather than blank. */
  values?: Record<string, string>;
}

export async function startCheckout(
  _previous: CheckoutState,
  form: FormData,
): Promise<CheckoutState> {
  const values = Object.fromEntries(
    [...form.entries()].map(([key, value]) => [key, typeof value === 'string' ? value : '']),
  ) as Record<string, string>;

  const name = values.name?.trim() ?? '';
  const email = values.email?.trim() ?? '';
  const phone = values.phone?.trim() ?? '';

  if (name.length === 0) {
    return { status: 'failed', message: 'Please tell us who the booking is for.', values };
  }

  if (!email.includes('@')) {
    return {
      status: 'failed',
      message: 'Please leave an email address — your booking and documents are sent there.',
      values,
    };
  }

  const cart = await getCart();

  if (!cart || cart.items.length === 0) {
    return { status: 'failed', message: 'Your cart is empty.', values };
  }

  // The names on each line. A flight or a bus needs one per traveller, exactly as their document
  // shows it; the API refuses the checkout and says which line is short if any are missing.
  const lines = cart.items.map((item) => ({
    cartItemId: item.id,
    travellers: travellersFor(values, item),
  }));

  const started = await commerce<CheckoutStarted>('/checkout', {
    method: 'POST',
    sessionToken: await cartToken(),
    body: { contact: { name, email, phone: phone.length > 0 ? phone : null }, lines },
  });

  if (!started.ok || !started.data) {
    return {
      status: 'failed',
      message:
        firstProblem(started.problem?.errors) ??
        started.problem?.detail ??
        started.problem?.title ??
        'We could not start your checkout just now. Nothing has been charged — please try again.',
      values,
    };
  }

  // The cart became the order the moment the API accepted the checkout, so the token in their
  // cookie now names something that is over. Dropping it here keeps the header honest and gives a
  // traveller who comes back without paying a fresh cart rather than a dead one.
  await forgetCart();

  // Off to the gateway. Nothing is charged until they finish there, and the booking is held until
  // the deadline the response carries.
  redirect(started.data.authorizationUrl);
}

/**
 * The travellers on one line, read out of the flat form.
 *
 * Fields are named `traveller-<cartItemId>-<index>-<field>`, because a form that works without
 * JavaScript cannot send nested objects. An empty set is sent for a line that needs no names — a
 * tour or a visa is booked against the lead contact, and the agency collects the rest later.
 */
function travellersFor(
  values: Record<string, string>,
  item: {
    id: string;
    itemType: string;
    adults: number | string;
    children: number | string;
    infants: number | string;
  },
): { type: string; firstName: string; lastName: string }[] {
  if (item.itemType !== 'Flight' && item.itemType !== 'Bus') {
    return [];
  }

  const types = [
    ...Array.from({ length: int64(item.adults) }, () => 'Adult'),
    ...Array.from({ length: int64(item.children) }, () => 'Child'),
    ...Array.from({ length: int64(item.infants) }, () => 'Infant'),
  ];

  return types.map((type, index) => ({
    type,
    firstName: values[`traveller-${item.id}-${index}-firstName`]?.trim() ?? '',
    lastName: values[`traveller-${item.id}-${index}-lastName`]?.trim() ?? '',
  }));
}

function firstProblem(errors: Record<string, string[]> | undefined): string | undefined {
  return errors ? Object.values(errors).flat()[0] : undefined;
}
