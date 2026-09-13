import { describe, expect, it } from 'vitest';
import { NAV_ITEMS, canManageVerification, navigationFor } from '../navigation';

/**
 * The sidebar is data, so what it offers each kind of user can be asserted without
 * rendering anything.
 */
describe('navigationFor', () => {
  const labels = (kind: 'principal' | 'sub_agent' | null, roles: string[]) =>
    navigationFor(kind, roles).flatMap((section) => section.items.map((item) => item.label));

  it('offers business verification to an owner', () => {
    expect(labels('principal', ['Owner'])).toContain('Business verification');
  });

  it('offers it to nobody else, because the API refuses them', () => {
    // Issue 172: every /api/v1/kyb route asks for `kyb.submit`, and of the agency
    // roles only the Owner holds it. A link that can only answer "no" is a dead end.
    expect(labels('principal', ['Manager'])).not.toContain('Business verification');
    expect(labels('principal', ['Agent'])).not.toContain('Business verification');
    expect(labels('principal', [])).not.toContain('Business verification');
  });

  it('still keeps a sub-agent out of the network it does not have', () => {
    expect(labels('principal', ['Owner'])).toContain('Sub-agents');
    expect(labels('sub_agent', ['Owner'])).not.toContain('Sub-agents');

    // The same endpoint serves both, with that agency's own figures alone.
    expect(labels('sub_agent', ['Owner'])).toContain('Network performance');
  });

  it('drops a section once everything in it is filtered away', () => {
    expect(navigationFor('sub_agent', ['Agent']).every((section) => section.items.length > 0)).toBe(
      true,
    );
  });
});

describe('canManageVerification', () => {
  it('reads the owner role by name', () => {
    expect(canManageVerification(['Owner'])).toBe(true);
    expect(canManageVerification(['Manager', 'Agent'])).toBe(false);
    expect(canManageVerification([])).toBe(false);
  });
});

describe('NAV_ITEMS', () => {
  it('names each path once', () => {
    const paths = NAV_ITEMS.map((item) => item.to);

    expect(new Set(paths).size).toBe(paths.length);
  });
});
