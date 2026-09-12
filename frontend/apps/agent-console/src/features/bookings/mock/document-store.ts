import { ApiError } from '../../../api/errors';
import type { BookingDetail, BookingDocument } from '../types';
import { findBooking } from './booking-store';
import { samplePdfDataUrl } from './sample-pdf';

/**
 * ============================================================================
 *  TEMPORARY. Delete with the rest of `mock/` when the orders API lands (#42, #44).
 * ============================================================================
 *
 * A booking's invoice and voucher, as the real endpoints return them (#46).
 * They appear for a ticketed booking, "being prepared" for a moment and then
 * ready — as the Worker renders them — and each opens a plain one-page PDF. A
 * reissue is a new document, one issue higher, that replaces the one before
 * without changing it; replacing the same document twice is refused.
 */

/** How long the stand-in takes to "render" a document. */
export const PREPARE_MS = 2_500;

interface StoredDocument {
  bookingReference: string;
  readyAt: number;
  document: BookingDocument;
}

type DocumentKind = 'Invoice' | 'Voucher';

const byBooking = new Map<string, StoredDocument[]>();
const counters: Record<DocumentKind, number> = { Invoice: 0, Voucher: 0 };

/** The booking's documents, issuing them the first time a ticketed booking is asked about. */
export function documentsFor(reference: string, now = Date.now()): BookingDocument[] {
  const booking = findBooking(reference);

  if (!booking) {
    throw new ApiError(
      404,
      'We could not find that booking.',
      'It may belong to another agency, or the reference may be mistyped.',
    );
  }

  let stored = byBooking.get(reference);

  if (!stored) {
    if (booking.status !== 'ticketed') return [];
    stored = [issue(booking, 'Invoice', now), issue(booking, 'Voucher', now)];
    byBooking.set(reference, stored);
  }

  settle(stored, now);
  return stored.map((entry) => structuredClone(entry.document));
}

/** Replaces a ready, current document with the next issue. The original is left as it was. */
export function reissueDocument(documentId: string, now = Date.now()): BookingDocument {
  for (const [reference, stored] of byBooking) {
    settle(stored, now);
    const original = stored.find((entry) => entry.document.id === documentId);
    if (!original) continue;

    if (original.document.supersededByDocumentId) {
      throw new ApiError(
        409,
        `That document has already been replaced by ${original.document.supersededByDocumentNumber}.`,
        'Reissue the newest one instead.',
      );
    }

    if (original.document.status !== 'Ready') {
      throw new ApiError(
        409,
        'That document is still being prepared.',
        'It can be reissued once its PDF is ready.',
      );
    }

    const booking = findBooking(reference);
    if (!booking) break;

    const replacement = issue(
      booking,
      original.document.documentType as DocumentKind,
      now,
      original.document,
    );

    // Only the pointer to its successor — which the real API works out rather than stores.
    original.document = {
      ...original.document,
      supersededByDocumentId: replacement.document.id,
      supersededByDocumentNumber: replacement.document.documentNumber,
    };
    stored.push(replacement);

    return structuredClone(replacement.document);
  }

  throw new ApiError(404, 'We could not find that document.');
}

/** For tests: forget every document, so each one starts afresh. */
export function resetDocumentStore(): void {
  byBooking.clear();
  counters.Invoice = 0;
  counters.Voucher = 0;
}

function issue(
  booking: BookingDetail,
  kind: DocumentKind,
  now: number,
  replaces?: BookingDocument,
): StoredDocument {
  counters[kind] += 1;

  return {
    bookingReference: booking.reference,
    readyAt: now + PREPARE_MS,
    document: {
      id: crypto.randomUUID(),
      documentType: kind,
      documentNumber: `${kind === 'Invoice' ? 'INV' : 'VCH'}-2026-${String(counters[kind]).padStart(6, '0')}`,
      issueNumber: replaces ? Number(replaces.issueNumber) + 1 : 1,
      status: 'Pending',
      productType: kind === 'Voucher' ? (booking.product === 'flight' ? 'Flight' : 'Bus') : null,
      issuedAt: new Date(now).toISOString(),
      supersedesDocumentNumber: replaces?.documentNumber ?? null,
      supersededByDocumentId: null,
      supersededByDocumentNumber: null,
      fileName: null,
      sizeBytes: null,
      checksum: null,
      downloadUrl: null,
      downloadExpiresAt: null,
      email: { recipient: emailFor(booking), status: 'queued', sentAt: null },
    },
  };
}

/** Anything whose moment has come is ready, with a file to open, and emailed. */
function settle(stored: StoredDocument[], now: number): void {
  for (const entry of stored) {
    const document = entry.document;
    if (document.status !== 'Pending' || entry.readyAt > now) continue;

    const booking = findBooking(entry.bookingReference);
    const url = samplePdfDataUrl([
      `${document.documentType} ${document.documentNumber}`,
      Number(document.issueNumber) > 1
        ? `Issue ${document.issueNumber}, replacing ${document.supersedesDocumentNumber}`
        : 'Issue 1',
      `Booking ${entry.bookingReference}`,
      booking ? `${booking.origin} to ${booking.destination}, ${booking.carrier}` : '',
      booking ? `For ${booking.leadTraveller}` : '',
      'A stand-in PDF: the real one is drawn in your own branding.',
    ]);

    entry.document = {
      ...document,
      status: 'Ready',
      fileName: `${document.documentNumber}.pdf`,
      sizeBytes: url.length,
      downloadUrl: url,
      downloadExpiresAt: new Date(now + 60 * 60_000).toISOString(),
      email: document.email
        ? { ...document.email, status: 'sent', sentAt: new Date(now).toISOString() }
        : null,
    };
  }
}

function emailFor(booking: BookingDetail): string {
  return `${booking.leadTraveller.toLowerCase().replace(/[^a-z]+/g, '.')}@example.test`;
}
