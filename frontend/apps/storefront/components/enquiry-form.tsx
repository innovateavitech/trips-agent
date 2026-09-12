'use client';

import { useActionState } from 'react';
import { sendEnquiry, type EnquiryState } from '../app/enquire/actions';

/**
 * The trip-request form (issue 62). Submitting it puts a new lead in the agency's CRM.
 *
 * A plain form posting to a server action, so it works before any JavaScript has loaded and on a
 * connection where some of it never will. `useActionState` upgrades it when the script is there:
 * the same form, with the pending state and the message rendered in place.
 */
export function EnquiryForm({ destination }: { destination?: string }) {
  const [state, action, pending] = useActionState<EnquiryState, FormData>(sendEnquiry, {
    status: 'idle',
  });

  if (state.status === 'sent') {
    return (
      <div className="rounded-lg border border-border bg-card p-6">
        <h2 className="text-lg font-semibold text-foreground">Thank you — we have your enquiry.</h2>
        <p className="mt-2 text-sm text-muted-foreground">
          One of us will be in touch shortly with some options and prices.
        </p>
      </div>
    );
  }

  const values = state.values ?? {};

  return (
    <form
      action={action}
      className="grid gap-4 rounded-lg border border-border bg-card p-6 sm:grid-cols-2"
    >
      {state.status === 'failed' && state.message ? (
        <p
          role="alert"
          className="sm:col-span-2 rounded-md bg-destructive-subtle px-3 py-2 text-sm text-destructive-subtle-foreground"
        >
          {state.message}
        </p>
      ) : null}

      <Field label="Your name" name="name" required defaultValue={values.name} />
      <Field
        label="Where would you like to go?"
        name="destination"
        required
        defaultValue={values.destination ?? destination}
      />
      <Field label="Email address" name="email" type="email" defaultValue={values.email} />
      <Field label="Phone number" name="phone" type="tel" defaultValue={values.phone} />
      <Field
        label="Travelling from"
        name="travelFrom"
        type="date"
        defaultValue={values.travelFrom}
      />
      <Field label="Coming back" name="travelTo" type="date" defaultValue={values.travelTo} />
      <Field
        label="Adults"
        name="adults"
        type="number"
        min={1}
        defaultValue={values.adults ?? '1'}
      />
      <Field
        label="Children"
        name="children"
        type="number"
        min={0}
        defaultValue={values.children ?? '0'}
      />

      <div className="sm:col-span-2">
        <Field
          label="Budget per person"
          hint="Optional, and only a guide — tell us roughly what you had in mind."
          name="budgetMax"
          type="number"
          min={0}
          defaultValue={values.budgetMax}
        />
      </div>

      <div className="sm:col-span-2">
        <label className="flex flex-col gap-1.5">
          <span className="text-sm font-medium text-foreground">Anything else we should know?</span>
          <textarea
            name="message"
            rows={4}
            defaultValue={values.message}
            className="w-full rounded-md border border-input bg-background px-3 py-2 text-sm text-foreground placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          />
        </label>
      </div>

      <div className="sm:col-span-2">
        <button
          type="submit"
          disabled={pending}
          className="inline-flex h-11 items-center justify-center rounded-md bg-primary px-6 text-sm font-medium text-primary-foreground transition-colors hover:bg-primary-hover disabled:opacity-60 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2"
        >
          {pending ? 'Sending…' : 'Send my enquiry'}
        </button>
      </div>
    </form>
  );
}

function Field({
  label,
  hint,
  name,
  type = 'text',
  required,
  min,
  defaultValue,
}: {
  label: string;
  hint?: string;
  name: string;
  type?: string;
  required?: boolean;
  min?: number;
  defaultValue?: string;
}) {
  return (
    <label className="flex flex-col gap-1.5">
      <span className="text-sm font-medium text-foreground">{label}</span>
      <input
        name={name}
        type={type}
        required={required}
        min={min}
        defaultValue={defaultValue}
        className="h-10 w-full rounded-md border border-input bg-background px-3 text-sm text-foreground placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
      />
      {hint ? <span className="text-xs text-muted-foreground">{hint}</span> : null}
    </label>
  );
}
