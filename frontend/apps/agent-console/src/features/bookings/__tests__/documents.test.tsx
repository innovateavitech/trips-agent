// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { API_BASE_URL } from '../../../api/config';
import { ApiError } from '../../../api/errors';
import { DocumentsCard } from '../components/documents-card';
import {
  currentVoucher,
  describeEmail,
  documentHref,
  isPreparing,
  orderDocuments,
} from '../documents-rules';
import { resetBookingStore } from '../mock/booking-store';
import {
  PREPARE_MS,
  documentsFor,
  reissueDocument,
  resetDocumentStore,
} from '../mock/document-store';
import type { BookingDocument } from '../types';

afterEach(cleanup);

function document(patch: Partial<BookingDocument>): BookingDocument {
  return {
    id: 'doc-invoice-1',
    documentType: 'Invoice',
    documentNumber: 'INV-2026-000042',
    issueNumber: 1,
    status: 'Ready',
    productType: null,
    issuedAt: '2026-10-01T10:00:00Z',
    supersedesDocumentNumber: null,
    supersededByDocumentId: null,
    supersededByDocumentNumber: null,
    fileName: 'INV-2026-000042.pdf',
    sizeBytes: 48_000,
    checksum: null,
    downloadUrl: '/api/v1/documents/doc-invoice-1/pdf?expires=1&signature=s',
    downloadExpiresAt: '2026-10-01T11:00:00Z',
    email: { recipient: 'ada@example.test', status: 'sent', sentAt: '2026-10-01T10:00:05Z' },
    ...patch,
  };
}

const voucher = document({
  id: 'doc-voucher-1',
  documentType: 'Voucher',
  documentNumber: 'VCH-2026-000007',
  productType: 'Flight',
  fileName: 'VCH-2026-000007.pdf',
  downloadUrl: '/api/v1/documents/doc-voucher-1/pdf?expires=1&signature=s',
});

describe('the documents rules', () => {
  it('puts the API in front of a relative link and leaves any other alone', () => {
    expect(documentHref('/api/v1/documents/x/pdf')).toBe(`${API_BASE_URL}/api/v1/documents/x/pdf`);
    expect(documentHref('https://files.example.test/x.pdf')).toBe(
      'https://files.example.test/x.pdf',
    );
    expect(documentHref('data:application/pdf;base64,AAAA')).toBe(
      'data:application/pdf;base64,AAAA',
    );
  });

  it('knows when to keep asking', () => {
    expect(isPreparing([document({}), voucher])).toBe(false);
    expect(isPreparing([document({ status: 'Pending' })])).toBe(true);
    expect(isPreparing(undefined)).toBe(false);
  });

  it('puts what is in use first, and what it replaced after', () => {
    const replaced = document({
      supersededByDocumentId: 'doc-invoice-2',
      supersededByDocumentNumber: 'INV-2026-000057',
    });
    const replacement = document({
      id: 'doc-invoice-2',
      documentNumber: 'INV-2026-000057',
      issueNumber: 2,
      supersedesDocumentNumber: 'INV-2026-000042',
    });

    expect(orderDocuments([replaced, voucher, replacement]).map((d) => d.documentNumber)).toEqual([
      'INV-2026-000057',
      'VCH-2026-000007',
      'INV-2026-000042',
    ]);
  });

  it('finds the voucher in use, never one it replaced', () => {
    const old = { ...voucher, supersededByDocumentId: 'doc-voucher-2' };
    const current = { ...voucher, id: 'doc-voucher-2', issueNumber: 2 };

    expect(currentVoucher([document({}), old, current])?.id).toBe('doc-voucher-2');
    expect(currentVoucher([document({})])).toBeNull();
  });

  it('puts a bounced email in words the agent can act on', () => {
    expect(describeEmail({ recipient: 'ada@x.test', status: 'bounced', sentAt: null })).toEqual({
      text: 'ada@x.test refused it — check the address with your customer',
      tone: 'destructive',
    });
    expect(describeEmail({ recipient: 'ada@x.test', status: 'queued', sentAt: null })?.tone).toBe(
      'neutral',
    );
    expect(describeEmail(null)).toBeNull();
  });
});

describe('DocumentsCard', () => {
  function renderCard(documents: BookingDocument[], onReissue = vi.fn()) {
    render(
      <DocumentsCard
        documents={documents}
        loading={false}
        problem={null}
        ticketed
        reissuingId={null}
        onReissue={onReissue}
      />,
    );
    return { onReissue };
  }

  it('downloads each ready document through its signed link', () => {
    renderCard([document({}), voucher]);

    const link = screen.getByRole('link', { name: 'Download invoice INV-2026-000042' });
    expect(link.getAttribute('href')).toBe(
      `${API_BASE_URL}/api/v1/documents/doc-invoice-1/pdf?expires=1&signature=s`,
    );
    expect(link.getAttribute('download')).toBe('INV-2026-000042.pdf');
    expect(screen.getByRole('link', { name: 'Download voucher VCH-2026-000007' })).toBeTruthy();
  });

  it('reissues only after saying the original stays exactly as it was', () => {
    const { onReissue } = renderCard([document({})]);

    fireEvent.click(screen.getByRole('button', { name: 'Reissue invoice INV-2026-000042' }));
    const dialog = screen.getByRole('dialog');

    expect(within(dialog).getByText(/stays on record exactly as it was issued/)).toBeTruthy();
    expect(onReissue).not.toHaveBeenCalled();

    fireEvent.click(within(dialog).getByRole('button', { name: 'Reissue' }));

    expect(onReissue).toHaveBeenCalledWith(expect.objectContaining({ id: 'doc-invoice-1' }));
  });

  it('does nothing when the agent backs out', () => {
    const { onReissue } = renderCard([document({})]);

    fireEvent.click(screen.getByRole('button', { name: 'Reissue invoice INV-2026-000042' }));
    fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Not now' }));

    expect(onReissue).not.toHaveBeenCalled();
  });

  it('keeps a replaced document downloadable but offers no second reissue of it', () => {
    renderCard([
      document({
        supersededByDocumentId: 'doc-invoice-2',
        supersededByDocumentNumber: 'INV-2026-000057',
      }),
    ]);

    expect(screen.getByText(/Replaced by INV-2026-000057/)).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Download invoice INV-2026-000042' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: /Reissue/ })).toBeNull();
  });

  it('says a document is being prepared, with nothing to click yet', () => {
    renderCard([document({ status: 'Pending', downloadUrl: null, fileName: null })]);

    expect(screen.getByText('Being prepared')).toBeTruthy();
    expect(screen.queryByRole('link')).toBeNull();
    expect(screen.queryByRole('button', { name: /Reissue/ })).toBeNull();
  });

  it('tells the agent when the customer never got it', () => {
    renderCard([
      document({ email: { recipient: 'ada@example.test', status: 'bounced', sentAt: null } }),
    ]);

    expect(screen.getByText(/ada@example.test refused it/)).toBeTruthy();
  });
});

describe('the stand-in documents', () => {
  const ticketed = 'TRP-8K2Q4F';
  const awaitingTicket = 'TRP-8K2P9A';

  beforeEach(() => {
    resetBookingStore();
    resetDocumentStore();
  });

  it('issues an invoice and a voucher for a ticketed booking, ready a moment later', () => {
    const now = Date.now();
    const pending = documentsFor(ticketed, now);

    expect(pending.map((d) => d.documentType)).toEqual(['Invoice', 'Voucher']);
    expect(pending.every((d) => d.status === 'Pending' && d.downloadUrl === null)).toBe(true);

    const ready = documentsFor(ticketed, now + PREPARE_MS);

    expect(ready.every((d) => d.status === 'Ready')).toBe(true);
    expect(ready[0]?.downloadUrl).toMatch(/^data:application\/pdf;base64,/);
    expect(ready[0]?.email?.status).toBe('sent');
  });

  it('has nothing for a booking that is not ticketed', () => {
    expect(documentsFor(awaitingTicket)).toEqual([]);
  });

  it('reissues as the next issue, and refuses to replace the same document twice', () => {
    const now = Date.now();
    documentsFor(ticketed, now);
    const [invoice] = documentsFor(ticketed, now + PREPARE_MS);

    const replacement = reissueDocument(invoice!.id, now + PREPARE_MS);

    expect(replacement.issueNumber).toBe(2);
    expect(replacement.supersedesDocumentNumber).toBe(invoice!.documentNumber);
    expect(replacement.status).toBe('Pending');

    const original = documentsFor(ticketed, now + PREPARE_MS).find((d) => d.id === invoice!.id);
    expect(original?.supersededByDocumentNumber).toBe(replacement.documentNumber);
    expect(original?.downloadUrl).toBe(invoice!.downloadUrl);

    expect(() => reissueDocument(invoice!.id, now + PREPARE_MS)).toThrow(ApiError);
  });
});
