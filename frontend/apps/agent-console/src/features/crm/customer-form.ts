import type { Problems } from './crm-rules';
import { EMAIL } from './lead-form';
import type { CustomerKind, CustomerRequest } from './types';

/** The "Add customer" form, as typed. */
export interface CustomerForm {
  kind: CustomerKind;
  name: string;
  email: string;
  phone: string;
}

export const EMPTY_CUSTOMER_FORM: CustomerForm = {
  kind: 'individual',
  name: '',
  email: '',
  phone: '',
};

export const CUSTOMER_KINDS: ReadonlyArray<{ value: CustomerKind; label: string }> = [
  { value: 'individual', label: 'Individual' },
  { value: 'business', label: 'Business' },
];

export type BuiltCustomer =
  { ok: true; request: CustomerRequest } | { ok: false; errors: Problems };

/**
 * A customer needs a name and one way to reach them — an email or a phone
 * number. Every problem at once, so the agent fixes the form in one pass.
 */
export function buildCustomerRequest(form: CustomerForm): BuiltCustomer {
  const errors: Problems = {};
  const name = form.name.trim();
  const email = form.email.trim();
  const phone = form.phone.trim();

  if (!name) {
    errors['name'] =
      form.kind === 'business' ? 'What is the business called?' : 'What is their name?';
  }

  if (!email && !phone) {
    errors['email'] = 'Add an email or a phone number, so you can reach them.';
  } else {
    if (email && !EMAIL.test(email)) errors['email'] = 'That does not look like an email address.';
    // Digits, spaces, dashes, brackets and one leading +; 7 to 15 digits in all (E.164).
    const digits = phone.replace(/\D/g, '');
    if (phone && (!/^\+?[\d\s()-]+$/.test(phone) || digits.length < 7 || digits.length > 15)) {
      errors['phone'] = 'That does not look like a phone number.';
    }
  }

  if (Object.keys(errors).length > 0) return { ok: false, errors };
  return {
    ok: true,
    request: { kind: form.kind, name, email: email || null, phone: phone || null },
  };
}
