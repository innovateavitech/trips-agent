import { describe, expect, it } from 'vitest';
import { agencyToken, fakeJwt, staffToken } from '../../../test/fake-jwt';
import { hasPermission, initialsFor, isTripsStaff, readClaims } from '../claims';

describe('readClaims', () => {
  it('reads a staff token', () => {
    const claims = readClaims(staffToken());

    expect(claims?.email).toBe('ops@tripsagent.test');
    expect(claims?.roles).toEqual(['Operations Admin']);
    expect(claims?.permissions).toContain('kyb.review');
    expect(claims?.agencyId).toBeNull();
  });

  it('normalises a claim that occurs once into an array', () => {
    // A staff member with one role gets `"role": "Operations Admin"`, not an array of one.
    const claims = readClaims(
      fakeJwt({ sub: 'u1', role: 'Super Admin', permission: 'kyb.review' }),
    );

    expect(claims?.roles).toEqual(['Super Admin']);
    expect(claims?.permissions).toEqual(['kyb.review']);
  });

  it('reads the agency of an agency account', () => {
    expect(readClaims(agencyToken())?.agencyId).toBe('01a08e86-c373-7baf-9c01-000000000002');
  });

  it('decodes characters outside ASCII', () => {
    const claims = readClaims(fakeJwt({ sub: 'u1', email: 'adé.okonkwo@tripsagent.test' }));

    expect(claims?.email).toBe('adé.okonkwo@tripsagent.test');
  });

  it('treats missing role and permission claims as none, not as a broken token', () => {
    const claims = readClaims(fakeJwt({ sub: 'u1' }));

    expect(claims?.roles).toEqual([]);
    expect(claims?.permissions).toEqual([]);
  });

  it.each([
    ['empty', ''],
    ['not a JWT', 'nonsense'],
    ['two segments', 'header.payload'],
    ['payload that is not base64', 'header.@@@@.signature'],
    ['payload that is not an object', fakeJwt(null as unknown as Record<string, unknown>)],
    ['no subject', fakeJwt({ email: 'ops@tripsagent.test' })],
  ])('refuses a token that is %s', (_case, token) => {
    expect(readClaims(token)).toBeNull();
  });
});

describe('isTripsStaff', () => {
  it('accepts a platform account holding a platform permission', () => {
    expect(isTripsStaff(readClaims(staffToken())!)).toBe(true);
  });

  it('refuses an agency owner', () => {
    expect(isTripsStaff(readClaims(agencyToken())!)).toBe(false);
  });

  it('refuses an agency account even if it somehow holds a platform permission', () => {
    // A misconfigured role must not open the back office to a travel agency.
    const token = agencyToken({ permission: ['booking.search', 'kyb.review'] });

    expect(isTripsStaff(readClaims(token)!)).toBe(false);
  });

  it('refuses a platform account with no platform permission at all', () => {
    const token = fakeJwt({ sub: 'u1', permission: ['report.view'] });

    expect(isTripsStaff(readClaims(token)!)).toBe(false);
  });
});

describe('hasPermission', () => {
  it('is exact — a prefix is not a match', () => {
    const claims = readClaims(staffToken({ permission: ['kyb.review.all'] }))!;

    expect(hasPermission(claims, 'kyb.review')).toBe(false);
  });
});

describe('initialsFor', () => {
  it.each([
    ['ops@tripsagent.test', 'OP'],
    ['ada.okonkwo@tripsagent.test', 'AO'],
    ['chidi_balogun@tripsagent.test', 'CB'],
    ['a@tripsagent.test', 'A'],
    ['', '?'],
  ])('turns %o into %o', (email, expected) => {
    expect(initialsFor(email)).toBe(expected);
  });
});
