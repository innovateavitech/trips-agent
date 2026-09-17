import { describe, expect, it } from 'vitest';
import { EMPTY_CUSTOMER_FORM, buildCustomerRequest, type CustomerForm } from '../customer-form';

const form = (patch: Partial<CustomerForm>): CustomerForm => ({ ...EMPTY_CUSTOMER_FORM, ...patch });

describe('the add-customer form', () => {
  it('builds a trimmed request, with blanks sent as null', () => {
    expect(
      buildCustomerRequest(
        form({ kind: 'business', name: '  Kanem Energy ', email: ' admin@kanem.test ', phone: '' }),
      ),
    ).toEqual({
      ok: true,
      request: { kind: 'business', name: 'Kanem Energy', email: 'admin@kanem.test', phone: null },
    });
  });

  it('needs a name, and asks for it in the words of the customer type', () => {
    const person = buildCustomerRequest(form({ phone: '0803 000 1122' }));
    const business = buildCustomerRequest(form({ kind: 'business', phone: '0803 000 1122' }));
    expect(!person.ok && person.errors['name']).toBe('What is their name?');
    expect(!business.ok && business.errors['name']).toBe('What is the business called?');
  });

  it('needs an email or a phone number, and either one alone is enough', () => {
    const neither = buildCustomerRequest(form({ name: 'Ada' }));
    expect(!neither.ok && Object.keys(neither.errors)).toEqual(['email']);
    expect(buildCustomerRequest(form({ name: 'Ada', email: 'ada@example.test' })).ok).toBe(true);
    expect(buildCustomerRequest(form({ name: 'Ada', phone: '+234 803 000 1122' })).ok).toBe(true);
  });

  it('turns away a mistyped email or phone number, reporting both at once', () => {
    const built = buildCustomerRequest(form({ name: 'Ada', email: 'ada@', phone: '0803-abc' }));
    expect(!built.ok && Object.keys(built.errors).sort()).toEqual(['email', 'phone']);
  });

  it('accepts a phone number written the usual Nigerian ways', () => {
    for (const phone of ['08030001122', '0803 000 1122', '+234 803 000 1122', '(0803) 000-1122']) {
      expect(buildCustomerRequest(form({ name: 'Ada', phone })).ok).toBe(true);
    }
    expect(buildCustomerRequest(form({ name: 'Ada', phone: '12345' })).ok).toBe(false);
  });
});
