'use server';

import { submitTripRequest } from '../../lib/api';

/**
 * The traveller's enquiry, sent from their browser to the agency's CRM (issue 62).
 *
 * A server action rather than a `fetch` from the page, for two reasons. The form works with
 * JavaScript switched off, which matters on the connections this audience actually has. And the API
 * is called from our server, so its address never reaches a browser and the hostname it is told is
 * the one the request arrived on — not one a caller chose.
 */

export interface EnquiryState {
  status: 'idle' | 'sent' | 'failed';
  message?: string;
  /** What they typed, so a refused form comes back filled in rather than blank. */
  values?: Record<string, string>;
}

export async function sendEnquiry(_previous: EnquiryState, form: FormData): Promise<EnquiryState> {
  const values = Object.fromEntries(
    [...form.entries()].map(([key, value]) => [key, typeof value === 'string' ? value : '']),
  ) as Record<string, string>;

  const name = values.name?.trim() ?? '';
  const email = values.email?.trim() ?? '';
  const phone = values.phone?.trim() ?? '';
  const destination = values.destination?.trim() ?? '';

  if (name.length === 0 || destination.length === 0) {
    return {
      status: 'failed',
      message: 'Please tell us your name and where you would like to go.',
      values,
    };
  }

  if (email.length === 0 && phone.length === 0) {
    return {
      status: 'failed',
      message: 'Please leave an email address or a phone number so we can reply.',
      values,
    };
  }

  const { ok } = await submitTripRequest({
    name,
    email: email.length > 0 ? email : null,
    phone: phone.length > 0 ? phone : null,
    destination,
    travelFrom: blankToNull(values.travelFrom),
    travelTo: blankToNull(values.travelTo),
    adults: wholeNumber(values.adults, 1),
    children: wholeNumber(values.children, 0),
    budgetMinMinor: null,
    budgetMaxMinor: toMinorUnits(values.budgetMax),
    message: values.message?.trim() ?? '',
  });

  return ok
    ? { status: 'sent' }
    : {
        status: 'failed',
        message: 'We could not send that just now. Please try again, or call us.',
        values,
      };
}

function blankToNull(value: string | undefined): string | null {
  const trimmed = value?.trim() ?? '';
  return trimmed.length > 0 ? trimmed : null;
}

function wholeNumber(value: string | undefined, fallback: number): number {
  const parsed = Number(value);
  return Number.isSafeInteger(parsed) && parsed >= 0 ? parsed : fallback;
}

/**
 * A budget typed in naira becomes kobo, as every money figure crossing this boundary must
 * (CLAUDE.md rule 2). Rounded to the nearest kobo rather than truncated, and only ever done once.
 */
function toMinorUnits(value: string | undefined): number | null {
  const trimmed = value?.trim() ?? '';

  if (trimmed.length === 0) {
    return null;
  }

  const parsed = Number(trimmed.replace(/[, ]/g, ''));

  return Number.isFinite(parsed) && parsed >= 0 ? Math.round(parsed * 100) : null;
}
