import { parseAmount } from '../pricing/pricing-rules';
import type { Problems } from './crm-rules';
import type { LeadRequest } from './types';

/** The new-lead form, as typed. */
export interface LeadForm {
  name: string;
  email: string;
  phone: string;
  destination: string;
  travelFrom: string;
  travelTo: string;
  adults: string;
  children: string;
  budgetMin: string;
  budgetMax: string;
  message: string;
}

export const EMPTY_LEAD_FORM: LeadForm = {
  name: '',
  email: '',
  phone: '',
  destination: '',
  travelFrom: '',
  travelTo: '',
  adults: '2',
  children: '0',
  budgetMin: '',
  budgetMax: '',
  message: '',
};

export type BuiltLead = { ok: true; request: LeadRequest } | { ok: false; errors: Problems };

const EMAIL = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

/**
 * A lead needs a person, a way to reach them and somewhere they want to go —
 * everything else can be learned on the first call. Every problem at once.
 */
export function buildLeadRequest(form: LeadForm): BuiltLead {
  const errors: Problems = {};
  const email = form.email.trim();
  const phone = form.phone.trim();

  if (!form.name.trim()) errors['name'] = 'Who is the trip for?';
  if (!email && !phone) errors['email'] = 'An email or a phone number, so you can reach them.';
  else if (email && !EMAIL.test(email))
    errors['email'] = 'That does not look like an email address.';
  if (!form.destination.trim()) errors['destination'] = 'Where do they want to go?';

  const count = (field: string, input: string, min: number): number => {
    const trimmed = input.trim() || '0';
    if (/^\d+$/.test(trimmed) && Number(trimmed) >= min) return Number(trimmed);
    errors[field] = min === 1 ? 'At least one adult.' : 'A whole number.';
    return min;
  };
  const adults = count('adults', form.adults, 1);
  const children = count('children', form.children, 0);

  const money = (field: string, input: string): number | null => {
    if (!input.trim()) return null;
    const parsed = parseAmount(input);
    if (parsed.ok) return parsed.value;
    errors[field] = parsed.error;
    return null;
  };
  const budgetMinMinor = money('budgetMin', form.budgetMin);
  const budgetMaxMinor = money('budgetMax', form.budgetMax);
  if (budgetMinMinor !== null && budgetMaxMinor !== null && budgetMaxMinor < budgetMinMinor) {
    errors['budgetMax'] = 'Less than the lowest figure.';
  }

  if (form.travelFrom && form.travelTo && form.travelTo < form.travelFrom) {
    errors['travelTo'] = 'Before the day they leave.';
  }

  if (Object.keys(errors).length > 0) return { ok: false, errors };

  return {
    ok: true,
    request: {
      customer: { name: form.name.trim(), email: email || null, phone: phone || null },
      destination: form.destination.trim(),
      travelFrom: form.travelFrom || null,
      travelTo: form.travelTo || null,
      adults,
      children,
      budgetMinMinor,
      budgetMaxMinor,
      message: form.message.trim(),
    },
  };
}
