import { describe, expect, it } from 'vitest';
import { HOME_PATH, safeRedirect } from '../redirect';

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
