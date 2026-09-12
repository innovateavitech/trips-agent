import { describe, expect, it } from 'vitest';
import { HOME_PATH, safeRedirect } from '../redirect';
import type { StaffClaims } from '../claims';

describe('safeRedirect', () => {
  it('returns to the page that was asked for', () => {
    expect(safeRedirect('/kyb/01a08e86-c373-7baf-9c01-88c8568ea867')).toBe(
      '/kyb/01a08e86-c373-7baf-9c01-88c8568ea867',
    );
  });

  it('keeps the query string, so a filtered queue comes back filtered', () => {
    expect(safeRedirect('/kyb?status=UnderReview')).toBe('/kyb?status=UnderReview');
  });

  it.each([
    ['nothing asked for', undefined],
    ['a value that is not a string', 42],
    ['another site', 'https://evil.example/steal'],
    ['a protocol-relative URL', '//evil.example/steal'],
    ['a backslash trick some browsers follow', '/\\evil.example'],
    ['the sign-in page itself', '/sign-in'],
    ['the sign-in page with a query', '/sign-in?from=/kyb'],
  ])('falls back to the home page for %s', (_case, from) => {
    expect(safeRedirect(from)).toBe(HOME_PATH);
  });
});

describe('safeRedirect with claims', () => {
  function claims(permissions: string[]): StaffClaims {
    return {
      userId: '11111111-1111-1111-1111-111111111111',
      email: 'zainab@tripsagent.example.com',
      roles: ['Support Admin'],
      permissions,
      agencyId: null,
    };
  }

  it('lands a support account on the one screen it can open, not on a refusal', () => {
    // Support holds agency.view and nothing else. A fixed home page would refuse them at every
    // sign-in, which is the bug this argument exists to stop.
    expect(safeRedirect(undefined, claims(['agency.view']))).toBe('/agencies');
  });

  it('sends somebody with the numbers to the dashboard', () => {
    expect(safeRedirect(undefined, claims(['agency.view', 'platform.report.view']))).toBe(
      '/dashboard',
    );
  });

  it('sends an operations account to the queue waiting on them', () => {
    expect(safeRedirect(undefined, claims(['agency.view', 'kyb.review']))).toBe('/kyb');
  });

  it('still follows where they were heading — claims only choose the fallback', () => {
    expect(safeRedirect('/audit', claims(['agency.view', 'platform.report.view']))).toBe('/audit');
  });

  it('never lets claims widen what a dangerous `from` is allowed to be', () => {
    expect(safeRedirect('//evil.example', claims(['agency.view']))).toBe('/agencies');
    expect(safeRedirect('/sign-in', claims(['agency.view', 'kyb.review']))).toBe('/kyb');
  });
});
