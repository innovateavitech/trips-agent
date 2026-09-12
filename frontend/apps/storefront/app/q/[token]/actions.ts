'use server';

import { revalidatePath } from 'next/cache';
import { respondToQuote } from '../../../lib/api';

/**
 * The customer's answer to a quote (issue 62).
 *
 * A server action, so the accept and decline buttons are real form submissions that work without
 * JavaScript. Whichever way they answer, the page is re-read afterwards so it shows the quote as it
 * now stands rather than what the button claimed.
 */

export interface QuoteAnswerState {
  status: 'idle' | 'failed';
  message?: string;
}

export async function answerQuote(
  _previous: QuoteAnswerState,
  form: FormData,
): Promise<QuoteAnswerState> {
  const token = String(form.get('token') ?? '');
  const answer = form.get('answer') === 'accept' ? 'accept' : 'decline';
  const reason = String(form.get('reason') ?? '').trim();

  if (token.length === 0) {
    return {
      status: 'failed',
      message: 'That link is not complete. Please open it again from your email.',
    };
  }

  const { ok } = await respondToQuote(token, answer, reason.length > 0 ? reason : undefined);

  if (!ok) {
    return {
      status: 'failed',
      message:
        'We could not record that just now. The quote may have expired, or already been answered — please refresh, or reply to our email.',
    };
  }

  revalidatePath(`/q/${token}`);

  return { status: 'idle' };
}
