import { describe, expect, it } from 'vitest';
import {
  displayName,
  isReadOnly,
  isSelf,
  nextStatus,
  summarise,
  userStatusDisplay,
} from '../back-office-rules';
import type { PlatformRole, PlatformUser } from '../types';

function user(overrides: Partial<PlatformUser> = {}): PlatformUser {
  return {
    id: '11111111-1111-1111-1111-111111111111',
    email: 'zainab@tripsagent.example.com',
    firstName: 'Zainab',
    lastName: 'Suleiman',
    status: 'Active',
    roles: ['Support Admin'],
    permissions: ['agency.view', 'customer.view'],
    lastLoginAt: null,
    createdAt: '2026-09-01T09:00:00Z',
    ...overrides,
  };
}

function role(name: string, permissions: string[]): PlatformRole {
  return {
    id: `role-${name}`,
    name,
    description: 'Seeded description from the API.',
    permissions,
  };
}

describe('userStatusDisplay', () => {
  it('says an invited account cannot sign in yet, which is what is asked about it', () => {
    expect(userStatusDisplay('Invited').meaning).toContain('cannot sign in');
  });

  it('says a suspension is reversible and a deactivation is not', () => {
    expect(userStatusDisplay('Suspended').meaning).toContain('reactivated');
    expect(userStatusDisplay('Deactivated').meaning).toContain('for good');
  });
});

describe('nextStatus', () => {
  it('offers suspension for an account that can currently be used', () => {
    expect(nextStatus('Active')).toEqual({ to: 'Suspended', label: 'Suspend', destructive: true });
    expect(nextStatus('Invited')?.to).toBe('Suspended');
  });

  it('offers reactivation for a suspended one', () => {
    expect(nextStatus('Suspended')).toEqual({
      to: 'Active',
      label: 'Reactivate',
      destructive: false,
    });
  });

  it('never offers deactivation, which cannot be undone from this screen', () => {
    expect(nextStatus('Active')?.to).not.toBe('Deactivated');
    expect(nextStatus('Deactivated')).toBeNull();
  });
});

describe('isSelf', () => {
  it('recognises the signed-in person, so the screen never offers them their own suspension', () => {
    expect(isSelf(user(), '11111111-1111-1111-1111-111111111111')).toBe(true);
  });

  it('is false for everybody else, and for a session that has not loaded', () => {
    expect(isSelf(user(), '99999999-9999-9999-9999-999999999999')).toBe(false);
    expect(isSelf(user(), null)).toBe(false);
  });
});

describe('displayName', () => {
  it('uses the name when there is one', () => {
    expect(displayName(user())).toBe('Zainab Suleiman');
  });

  it('falls back to the email rather than rendering an empty cell', () => {
    expect(displayName(user({ firstName: '', lastName: '' }))).toBe(
      'zainab@tripsagent.example.com',
    );
  });
});

describe('summarise', () => {
  it('describes the permissions it has wording for', () => {
    const summary = summarise(role('Support Admin', ['agency.view']));

    expect(summary.can).toEqual(['read every agency']);
    expect(summary.unnamedCount).toBe(0);
  });

  it('counts the ones it cannot describe instead of pretending they are not there', () => {
    const summary = summarise(role('Support Admin', ['agency.view', 'customer.view']));

    expect(summary.can).toEqual(['read every agency']);
    expect(summary.unnamedCount).toBe(1);
  });
});

describe('isReadOnly', () => {
  it('marks the support role, which changes nothing about an agency', () => {
    expect(isReadOnly(role('Support Admin', ['agency.view', 'customer.view']))).toBe(true);
  });

  it('does not mark a role that can suspend, verify or grant', () => {
    expect(isReadOnly(role('Operations Admin', ['agency.view', 'kyb.review']))).toBe(false);
    expect(isReadOnly(role('Super Admin', ['platform.user.manage']))).toBe(false);
  });
});
