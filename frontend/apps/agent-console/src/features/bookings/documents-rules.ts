import type { BadgeProps } from '@trips/ui';
import { API_BASE_URL } from '../../api/config';
import type { BookingDocument } from './types';

/**
 * What the documents screens decide about a booking's invoices and vouchers
 * (#46), as plain functions — tested without rendering in
 * `__tests__/documents.test.tsx`.
 */

export const DOCUMENT_LABEL: Record<string, string> = {
  Invoice: 'Invoice',
  Voucher: 'Voucher',
};

/**
 * Where a document's link points. The API hands out a path relative to itself
 * while files are stored locally, and a full URL once a cloud store signs them;
 * a stand-in may hand out a `data:` URL. Only the first needs the API's address.
 */
export function documentHref(downloadUrl: string): string {
  return downloadUrl.startsWith('/') ? `${API_BASE_URL}${downloadUrl}` : downloadUrl;
}

/** True while any of them is still being prepared — worth asking again shortly. */
export function isPreparing(documents: readonly BookingDocument[] | undefined): boolean {
  return documents?.some((document) => document.status === 'Pending') ?? false;
}

/** True once another document has replaced this one. It stays on record, unchanged. */
export function isReplaced(document: BookingDocument): boolean {
  return document.supersededByDocumentId !== null && document.supersededByDocumentId !== undefined;
}

/**
 * The ones in use first — invoice, then vouchers — and after them the ones
 * they replaced, newest issue first. What an agent needs is at the top.
 */
export function orderDocuments(documents: readonly BookingDocument[]): BookingDocument[] {
  const rank = (document: BookingDocument) =>
    (isReplaced(document) ? 2 : 0) + (document.documentType === 'Invoice' ? 0 : 1);

  return [...documents].sort(
    (a, b) => rank(a) - rank(b) || Number(b.issueNumber) - Number(a.issueNumber),
  );
}

/** The voucher a traveller should use now, or `null` while there is none. */
export function currentVoucher(
  documents: readonly BookingDocument[] | undefined,
): BookingDocument | null {
  return (
    documents?.find((document) => document.documentType === 'Voucher' && !isReplaced(document)) ??
    null
  );
}

export interface EmailDescription {
  text: string;
  tone: NonNullable<BadgeProps['tone']>;
}

/** What happened to the email that carried a document, in the agent's words. */
export function describeEmail(email: BookingDocument['email']): EmailDescription | null {
  if (!email) return null;

  switch (email.status) {
    case 'sent':
      return { text: `Emailed to ${email.recipient}`, tone: 'success' };
    case 'delivered':
      return { text: `Delivered to ${email.recipient}`, tone: 'success' };
    case 'bounced':
      return {
        text: `${email.recipient} refused it — check the address with your customer`,
        tone: 'destructive',
      };
    case 'failed':
      return {
        text: `Could not be emailed to ${email.recipient} — download it and send it yourself`,
        tone: 'destructive',
      };
    default:
      return { text: `Emailing ${email.recipient}`, tone: 'neutral' };
  }
}
