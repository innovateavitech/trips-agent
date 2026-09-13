import { describe, expect, it } from 'vitest';
import {
  actionLabel,
  actionTone,
  actorTypeDisplay,
  changedFields,
  fieldLabel,
  stateIsUnreadable,
} from '../audit-rules';
import type { AuditLogEntry } from '../types';

function entry(overrides: Partial<AuditLogEntry> = {}): AuditLogEntry {
  return {
    id: '11111111-1111-1111-1111-111111111111',
    occurredAt: '2026-09-12T09:00:00Z',
    agencyId: null,
    agencyName: null,
    actorUserId: null,
    actorName: 'Ada Obi',
    actorType: 'PlatformAdmin',
    action: 'agency.suspended',
    entityType: 'agencies',
    entityId: '22222222-2222-2222-2222-222222222222',
    reason: 'Chargebacks under investigation.',
    beforeState: null,
    afterState: null,
    actorIpAddress: null,
    ...overrides,
  };
}

describe('actionLabel', () => {
  it('says the business actions the way a person would', () => {
    expect(actionLabel('agency.suspended')).toBe('Suspended');
    expect(actionLabel('agency.profile_updated')).toBe('Profile edited');
    expect(actionLabel('platform_user.role_changed')).toBe('Back-office role changed');
  });

  it('leaves an action it does not know alone rather than guessing at it', () => {
    expect(actionLabel('wallet.manually_adjusted')).toBe('wallet.manually_adjusted');
  });
});

describe('actionTone', () => {
  it('draws only the actions that end or interrupt a relationship as destructive', () => {
    expect(actionTone('agency.suspended')).toBe('destructive');
    expect(actionTone('agency.terminated')).toBe('destructive');
    expect(actionTone('deleted')).toBe('destructive');
  });

  it('leaves routine edits plain, so a termination still stands out', () => {
    expect(actionTone('agency.profile_updated')).toBe('neutral');
    expect(actionTone('updated')).toBe('neutral');
  });

  it('marks an export as worth noticing without calling it a disaster', () => {
    expect(actionTone('agency.exported')).toBe('warning');
  });
});

describe('actorTypeDisplay', () => {
  it('separates a Trips admin from an agency acting on itself', () => {
    expect(actorTypeDisplay('PlatformAdmin').label).toBe('Trips staff');
    expect(actorTypeDisplay('User').label).toBe('Agency staff');
  });

  it('treats an unattributed row as the bug the domain says it is', () => {
    expect(actorTypeDisplay('Unknown').tone).toBe('destructive');
  });
});

describe('changedFields', () => {
  it('lists only what actually moved', () => {
    const changes = changedFields(
      entry({
        beforeState: JSON.stringify({ status: 'Verified', legal_name: 'Lagos Travel Limited' }),
        afterState: JSON.stringify({ status: 'Suspended', legal_name: 'Lagos Travel Limited' }),
      }),
    );

    expect(changes).toEqual([{ field: 'status', before: 'Verified', after: 'Suspended' }]);
  });

  it('reports a field that appears, as an insert does, with nothing before it', () => {
    const changes = changedFields(
      entry({ beforeState: null, afterState: JSON.stringify({ slug: 'lagos-travel' }) }),
    );

    expect(changes).toEqual([{ field: 'slug', before: null, after: 'lagos-travel' }]);
  });

  it('keeps numbers and booleans readable rather than dropping them', () => {
    const changes = changedFields(
      entry({
        beforeState: JSON.stringify({ vat_rate_basis_points: 750, is_active: true }),
        afterState: JSON.stringify({ vat_rate_basis_points: 0, is_active: false }),
      }),
    );

    expect(changes).toEqual([
      { field: 'is_active', before: 'true', after: 'false' },
      { field: 'vat_rate_basis_points', before: '750', after: '0' },
    ]);
  });

  it('keeps a nested value as JSON instead of flattening it to [object Object]', () => {
    const changes = changedFields(
      entry({
        beforeState: JSON.stringify({ address: { city: 'Lagos' } }),
        afterState: JSON.stringify({ address: { city: 'Abuja' } }),
      }),
    );

    expect(changes[0]?.after).toBe('{"city":"Abuja"}');
  });

  it('does not throw on a state that is not JSON, so the rest of the entry still renders', () => {
    expect(() =>
      changedFields(entry({ beforeState: '{not json', afterState: null })),
    ).not.toThrow();
    expect(changedFields(entry({ beforeState: '{not json', afterState: null }))).toEqual([]);
  });

  it('is empty when an action recorded no state at all, as an export does', () => {
    expect(changedFields(entry({ action: 'agency.exported' }))).toEqual([]);
  });
});

describe('stateIsUnreadable', () => {
  it('is false when there was never any state to read', () => {
    expect(stateIsUnreadable(entry())).toBe(false);
  });

  it('is true only when text is present and none of it parses', () => {
    expect(stateIsUnreadable(entry({ afterState: '{not json' }))).toBe(true);
    expect(stateIsUnreadable(entry({ afterState: JSON.stringify({ a: 1 }) }))).toBe(false);
  });
});

describe('fieldLabel', () => {
  it('turns a column name into something readable', () => {
    expect(fieldLabel('vat_rate_basis_points')).toBe('Vat rate basis points');
    expect(fieldLabel('statusChangedAt')).toBe('Status changed at');
    expect(fieldLabel('status')).toBe('Status');
  });
});
