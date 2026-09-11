/**
 * Builds tokens that look like ours for tests.
 *
 * Unsigned — nothing in the console verifies a signature, and nothing should: the API holds the
 * key and checks it on every request. These exist only to exercise the claim reading.
 */
export function fakeJwt(claims: Record<string, unknown>): string {
  const header = base64Url(JSON.stringify({ alg: 'HS256', typ: 'JWT' }));
  return `${header}.${base64Url(JSON.stringify(claims))}.not-a-real-signature`;
}

/** A Trips Operations Admin, as the API issues one today. */
export function staffToken(overrides: Record<string, unknown> = {}): string {
  return fakeJwt({
    sub: '01a08e86-c373-7baf-9c01-88c8568ea867',
    email: 'ops@tripsagent.test',
    role: 'Operations Admin',
    permission: ['agency.manage', 'customer.view', 'kyb.review', 'platform.report.view'],
    ...overrides,
  });
}

/** An agency owner: carries an agency, holds no platform permission. */
export function agencyToken(overrides: Record<string, unknown> = {}): string {
  return fakeJwt({
    sub: '01a08e86-c373-7baf-9c01-000000000001',
    email: 'owner@lagostravel.test',
    agency_id: '01a08e86-c373-7baf-9c01-000000000002',
    role: 'Owner',
    permission: ['booking.search', 'wallet.view'],
    ...overrides,
  });
}

function base64Url(value: string): string {
  const bytes = new TextEncoder().encode(value);
  let binary = '';
  bytes.forEach((byte) => {
    binary += String.fromCharCode(byte);
  });
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}
