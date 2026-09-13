import { describe, expect, it } from 'vitest';
import { MINIMUM_REASON, canErase, describeChanges, statusTone } from '../erasure-rules';
import type { ErasurePreview, ErasureResult } from '../types';

function person(overrides: Partial<ErasurePreview> = {}): ErasurePreview {
  return {
    customerId: 'b1ec0000-0000-7000-8000-000000000001',
    name: 'Adaeze Okafor',
    email: 'adaeze@example.test',
    orders: 2,
    travellers: 3,
    travelDocuments: 1,
    notifications: 4,
    evidenceFiles: 0,
    blockers: [],
    ...overrides,
  };
}

const REASON = 'She asked us to erase her details, by email on 12 September.';

describe('canErase', () => {
  it('needs somebody found, a reason and the confirmation', () => {
    expect(canErase(person(), REASON, true)).toBe(true);
  });

  it('refuses before anybody has been looked up', () => {
    expect(canErase(null, REASON, true)).toBe(false);
  });

  it('refuses while something stands in the way', () => {
    const blocked = person({ blockers: ['1 order is still being paid for.'] });

    expect(canErase(blocked, REASON, true)).toBe(false);
  });

  it('refuses a reason too short to be one', () => {
    expect(canErase(person(), 'asked', true)).toBe(false);
    expect(canErase(person(), '   '.padEnd(MINIMUM_REASON + 5, ' '), true)).toBe(false);
  });

  it('refuses until the person running it says they know it cannot be undone', () => {
    expect(canErase(person(), REASON, false)).toBe(false);
  });
});

describe('describeChanges', () => {
  it('lists the tables that changed, with their counts', () => {
    const result: ErasureResult = {
      requestId: 'b1ec0000-0000-7000-8000-000000000002',
      completed: true,
      changed: { 'orders.order_travellers': 2, 'crm.customers': 1, 'payments.disputes': 0 },
      blockers: [],
    };

    expect(describeChanges(result)).toBe('crm.customers (1), orders.order_travellers (2)');
  });

  it('says so when there was nothing left to change', () => {
    const result: ErasureResult = {
      requestId: 'b1ec0000-0000-7000-8000-000000000003',
      completed: true,
      changed: { 'crm.customers': 0 },
      blockers: [],
    };

    expect(describeChanges(result)).toBe('nothing was left to change');
  });
});

describe('statusTone', () => {
  it('separates a completed erasure from a refused one', () => {
    expect(statusTone('Completed')).toBe('success');
    expect(statusTone('Refused')).toBe('warning');
    expect(statusTone('Requested')).toBe('warning');
  });
});
