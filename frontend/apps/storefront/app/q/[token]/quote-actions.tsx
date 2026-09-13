'use client';

import { useActionState, useState } from 'react';
import { answerQuote, type QuoteAnswerState } from './actions';

/**
 * Accept or decline, on the quote itself.
 *
 * Declining opens a box for a reason, which is optional. It is asked for because "too expensive" and
 * "wrong dates" send the agency in completely different directions, and the customer is the only
 * person who knows which it was.
 */
export function QuoteActions({ token }: { token: string }) {
  const [state, action, pending] = useActionState<QuoteAnswerState, FormData>(answerQuote, {
    status: 'idle',
  });
  const [declining, setDeclining] = useState(false);

  return (
    <div className="mt-8 rounded-lg border border-border bg-card p-6">
      {state.status === 'failed' && state.message ? (
        <p
          role="alert"
          className="mb-4 rounded-md bg-destructive-subtle px-3 py-2 text-sm text-destructive-subtle-foreground"
        >
          {state.message}
        </p>
      ) : null}

      <form action={action} className="flex flex-col gap-4">
        <input type="hidden" name="token" value={token} />

        {declining ? (
          <label className="flex flex-col gap-1.5">
            <span className="text-sm font-medium text-foreground">
              Could you tell us why? It helps us get closer next time.
            </span>
            <textarea
              name="reason"
              rows={3}
              className="w-full rounded-md border border-input bg-background px-3 py-2 text-sm text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            />
          </label>
        ) : null}

        <div className="flex flex-wrap gap-3">
          <button
            type="submit"
            name="answer"
            value="accept"
            disabled={pending}
            className="inline-flex h-11 items-center justify-center rounded-md bg-primary px-6 text-sm font-medium text-primary-foreground transition-colors hover:bg-primary-hover disabled:opacity-60 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2"
          >
            {pending ? 'Sending…' : 'Accept this quote'}
          </button>

          {declining ? (
            <button
              type="submit"
              name="answer"
              value="decline"
              disabled={pending}
              className="inline-flex h-11 items-center justify-center rounded-md border border-input px-6 text-sm font-medium text-foreground transition-colors hover:bg-muted disabled:opacity-60"
            >
              Send
            </button>
          ) : (
            <button
              type="button"
              onClick={() => setDeclining(true)}
              className="inline-flex h-11 items-center justify-center rounded-md px-4 text-sm font-medium text-muted-foreground transition-colors hover:text-foreground"
            >
              No thank you
            </button>
          )}
        </div>
      </form>
    </div>
  );
}
