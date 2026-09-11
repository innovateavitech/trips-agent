import { describe, expect, it } from 'vitest';
import {
  LINK_RENEWAL_LEAD_MS,
  MIN_RENEWAL_DELAY_MS,
  documentTypeLabel,
  formatFileSize,
  isSafeDocumentUrl,
  linkRenewalDelay,
  previewKind,
} from '../document-display';

const NOW = Date.parse('2026-09-11T09:00:00Z');
const expiringIn = (ms: number) => ({ urlExpiresAt: new Date(NOW + ms).toISOString() });

describe('documentTypeLabel', () => {
  it('names the types we ask for', () => {
    expect(documentTypeLabel('CertificateOfIncorporation')).toBe('Certificate of incorporation');
    expect(documentTypeLabel('TaxIdentification')).toBe('Tax identification (TIN)');
  });

  it('still reads well for a type this build has not heard of', () => {
    expect(documentTypeLabel('BoardResolution')).toBe('Board resolution');
  });
});

describe('formatFileSize', () => {
  it.each([
    [0, '0 B'],
    [512, '512 B'],
    [2048, '2 KB'],
    [10 * 1024 * 1024, '10.0 MB'],
    [1_572_864, '1.5 MB'],
  ])('reads %i bytes as %o', (bytes, expected) => {
    expect(formatFileSize(bytes)).toBe(expected);
  });

  it('says so rather than showing nonsense for an impossible size', () => {
    expect(formatFileSize(-1)).toBe('Unknown size');
    expect(formatFileSize(Number.NaN)).toBe('Unknown size');
  });
});

describe('previewKind', () => {
  it.each([
    ['application/pdf', 'pdf'],
    ['image/png', 'image'],
    ['image/jpeg', 'image'],
    ['IMAGE/PNG', 'image'],
    ['application/pdf; charset=binary', 'pdf'],
    ['application/octet-stream', 'none'],
    ['', 'none'],
  ])('previews %o as %o', (contentType, expected) => {
    expect(previewKind(contentType)).toBe(expected);
  });
});

describe('isSafeDocumentUrl', () => {
  it('accepts the signed path the API hands back', () => {
    expect(
      isSafeDocumentUrl(
        '/api/v1/admin/kyb/documents/01a08e86-c373-7baf-9c01-88c8568ea867?expires=1757580000&signature=abc123',
      ),
    ).toBe(true);
  });

  it.each([
    ['another origin', 'https://evil.example/document.pdf'],
    ['a protocol-relative URL', '//evil.example/document.pdf'],
    ['a script URL', 'javascript:alert(1)'],
    ['a different endpoint', '/api/v1/auth/me'],
    ['a backslash trick', '/api/v1/admin/kyb/documents/..\\..\\secret'],
  ])('refuses %s', (_case, url) => {
    expect(isSafeDocumentUrl(url)).toBe(false);
  });
});

describe('linkRenewalDelay', () => {
  it('renews a minute before the earliest link dies', () => {
    const delay = linkRenewalDelay([expiringIn(10 * 60_000), expiringIn(6 * 60_000)], NOW);

    expect(delay).toBe(6 * 60_000 - LINK_RENEWAL_LEAD_MS);
  });

  it('never asks for a refetch faster than the floor', () => {
    // If the browser's clock runs ahead, links look expired on arrival. Without the floor the
    // page would refetch in a tight loop.
    expect(linkRenewalDelay([expiringIn(-60_000)], NOW)).toBe(MIN_RENEWAL_DELAY_MS);
  });

  it('does nothing when there are no documents', () => {
    expect(linkRenewalDelay([], NOW)).toBe(false);
  });

  it('ignores an expiry it cannot read', () => {
    expect(linkRenewalDelay([{ urlExpiresAt: 'not a date' }], NOW)).toBe(false);
  });
});
