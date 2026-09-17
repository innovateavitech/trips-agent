import type { BadgeProps } from '@trips/ui';
import type { InvoiceStatus } from './types';

/**
 * How an invoice's status reads wherever one is listed — the Invoices screen
 * and a customer's Invoices tab — so the two can never disagree.
 */
export const INVOICE_STATUS_DISPLAY: Record<
  InvoiceStatus,
  { label: string; tone: NonNullable<BadgeProps['tone']> }
> = {
  overdue: { label: 'Overdue', tone: 'destructive' },
  pending: { label: 'Pending', tone: 'warning' },
  draft: { label: 'Draft', tone: 'neutral' },
  paid: { label: 'Paid', tone: 'success' },
};
