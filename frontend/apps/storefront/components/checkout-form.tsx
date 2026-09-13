'use client';

import { useActionState } from 'react';
import { formatMoney, int64 } from '@trips/utils';
import type { Cart, CartItem } from '../lib/api';
import { startCheckout, type CheckoutState } from '../app/checkout/actions';

/**
 * Who is buying, and who is travelling.
 *
 * **There is no card field here, and there must never be one.** Pressing the button hands the
 * traveller to the payment gateway's own page, which is where the card is typed (decision 18).
 * Everything this form collects is a name and a way to reach somebody.
 */
export function CheckoutForm({ cart }: { cart: Cart }) {
  const [state, action, pending] = useActionState<CheckoutState, FormData>(startCheckout, {
    status: 'idle',
  });

  const values = state.values ?? {};

  return (
    <form action={action} className="mt-8 flex flex-col gap-8">
      {state.status === 'failed' && state.message ? (
        <p
          role="alert"
          className="rounded-md bg-destructive-subtle px-3 py-2 text-sm text-destructive-subtle-foreground"
        >
          {state.message}
        </p>
      ) : null}

      <fieldset className="flex flex-col gap-4">
        <legend className="mb-2 text-lg font-semibold tracking-tight text-foreground">
          Who is this booking for?
        </legend>

        <Field label="Full name" name="name" defaultValue={values.name} required />
        <Field
          label="Email address"
          name="email"
          type="email"
          defaultValue={values.email}
          required
          hint="Your booking, your documents and your link to manage it all go here."
        />
        <Field label="Phone number" name="phone" type="tel" defaultValue={values.phone} />
      </fieldset>

      {cart.items.filter(needsNames).map((item) => (
        <TravellerNames key={item.id} item={item} values={values} />
      ))}

      <div className="border-t border-border pt-6">
        <div className="flex items-baseline justify-between">
          <span className="text-sm text-muted-foreground">Total</span>
          <span className="text-2xl font-semibold text-foreground">
            {formatMoney(int64(cart.totalMinor), cart.currency)}
          </span>
        </div>

        <button
          type="submit"
          disabled={pending}
          className="mt-6 inline-flex h-11 w-full items-center justify-center rounded-md bg-primary px-6 text-sm font-medium text-primary-foreground transition-colors hover:bg-primary-hover disabled:opacity-60 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 sm:w-auto"
        >
          {pending ? 'Taking you to pay…' : 'Continue to payment'}
        </button>

        <p className="mt-3 text-xs text-muted-foreground">
          You will be taken to our payment provider&rsquo;s secure page to enter your card. We never
          see or store your card details.
        </p>
      </div>
    </form>
  );
}

/** A flight or a bus carries names on the ticket; a tour or a visa is booked against the contact. */
function needsNames(item: CartItem): boolean {
  return item.itemType === 'Flight' || item.itemType === 'Bus';
}

function TravellerNames({ item, values }: { item: CartItem; values: Record<string, string> }) {
  const types = [
    ...Array.from({ length: int64(item.adults) }, () => 'Adult'),
    ...Array.from({ length: int64(item.children) }, () => 'Child'),
    ...Array.from({ length: int64(item.infants) }, () => 'Infant'),
  ];

  return (
    <fieldset className="flex flex-col gap-4">
      <legend className="mb-2 text-lg font-semibold tracking-tight text-foreground">
        Travellers on {item.title}
      </legend>

      <p className="text-sm text-muted-foreground">
        Names must match each traveller&rsquo;s passport or ID exactly. A ticket in the wrong name
        cannot be changed once it is issued.
      </p>

      {types.map((type, index) => (
        <div key={`${item.id}-${index}`} className="grid gap-4 sm:grid-cols-2">
          <Field
            label={`${type} ${index + 1} — first name`}
            name={`traveller-${item.id}-${index}-firstName`}
            defaultValue={values[`traveller-${item.id}-${index}-firstName`]}
            required
          />
          <Field
            label={`${type} ${index + 1} — last name`}
            name={`traveller-${item.id}-${index}-lastName`}
            defaultValue={values[`traveller-${item.id}-${index}-lastName`]}
            required
          />
        </div>
      ))}
    </fieldset>
  );
}

function Field({
  label,
  name,
  type = 'text',
  defaultValue,
  required,
  hint,
}: {
  label: string;
  name: string;
  type?: string;
  defaultValue?: string;
  required?: boolean;
  hint?: string;
}) {
  return (
    <label className="flex flex-col gap-1.5">
      <span className="text-sm font-medium text-foreground">
        {label}
        {required ? <span className="text-muted-foreground"> *</span> : null}
      </span>
      <input
        type={type}
        name={name}
        defaultValue={defaultValue}
        required={required}
        autoComplete={autoCompleteFor(name, type)}
        className="h-11 w-full rounded-md border border-input bg-background px-3 text-sm text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
      />
      {hint ? <span className="text-xs text-muted-foreground">{hint}</span> : null}
    </label>
  );
}

/** What a browser may fill in for the buyer. Never for a traveller's name, which must be typed. */
function autoCompleteFor(name: string, type: string): string {
  if (name === 'name') {
    return 'name';
  }

  if (type === 'email') {
    return 'email';
  }

  if (type === 'tel') {
    return 'tel';
  }

  return 'off';
}
