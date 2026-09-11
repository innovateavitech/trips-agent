import type { KybReviewDocument } from './types';

/**
 * How documents are named, sized, previewed and kept openable.
 */

const DOCUMENT_TYPE_LABELS: Record<string, string> = {
  CertificateOfIncorporation: 'Certificate of incorporation',
  TaxIdentification: 'Tax identification (TIN)',
  ProofOfAddress: 'Proof of address',
  DirectorIdentification: "Director's ID",
  Other: 'Other document',
};

/** "CertificateOfIncorporation" → "Certificate of incorporation". Unknown types still read well. */
export function documentTypeLabel(documentType: string): string {
  const known = DOCUMENT_TYPE_LABELS[documentType];
  if (known) return known;

  const words = documentType.replace(/([a-z])([A-Z])/g, '$1 $2').toLowerCase();
  return words.charAt(0).toUpperCase() + words.slice(1);
}

/** Binary units, as every file dialog counts them — the 10MB limit is 10 × 1024 × 1024 bytes. */
export function formatFileSize(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes < 0) return 'Unknown size';
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${Math.max(1, Math.round(bytes / 1024))} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

export type PreviewKind = 'image' | 'pdf' | 'none';

/** Only the three accepted types preview inline (PDF, JPG, PNG — FRD §2.2). */
export function previewKind(contentType: string): PreviewKind {
  const type = contentType.split(';')[0]?.trim().toLowerCase();
  if (type === 'application/pdf') return 'pdf';
  if (type === 'image/png' || type === 'image/jpeg') return 'image';
  return 'none';
}

const DOCUMENT_PATH = '/api/v1/admin/kyb/documents/';

/**
 * Is this a document link we are willing to put in an `href` or an `iframe`?
 *
 * The API builds these links, so this should always pass. It is checked anyway because the value
 * goes straight into a clickable link and an iframe on a staff machine: anything that is not our
 * own relative document path — a `javascript:` URL, another origin, `//elsewhere` — is not shown.
 */
export function isSafeDocumentUrl(url: string): boolean {
  return url.startsWith(DOCUMENT_PATH) && !url.includes('//') && !url.includes('\\');
}

/** Renew this long before the earliest link dies, so a click never lands on an expired one. */
export const LINK_RENEWAL_LEAD_MS = 60_000;

/** Never refetch faster than this, whatever the clocks say. */
export const MIN_RENEWAL_DELAY_MS = 5_000;

/**
 * How long to wait before refetching a submission so its links stay valid, or `false` when
 * there is nothing to renew.
 *
 * The floor matters. If the browser's clock runs ahead of the server's, the links look already
 * expired on arrival, and without a floor the page would refetch in a tight loop.
 */
export function linkRenewalDelay(
  documents: ReadonlyArray<Pick<KybReviewDocument, 'urlExpiresAt'>>,
  now: number,
): number | false {
  const expiries = documents
    .map((document) => Date.parse(document.urlExpiresAt))
    .filter((expiry) => !Number.isNaN(expiry));

  if (expiries.length === 0) return false;

  const earliest = Math.min(...expiries);
  return Math.max(MIN_RENEWAL_DELAY_MS, earliest - LINK_RENEWAL_LEAD_MS - now);
}
