import type { InvoicesApi } from '../invoices-api';
import type { CustomerInvoice } from '../types';

/**
 * ============================================================================
 *  TEMPORARY. Delete this folder once a real customer-invoices read model
 *  exists on the backend.
 * ============================================================================
 */

const LATENCY_MS = 250;
const DAY = 24 * 60 * 60 * 1000;

function issuedDaysAgo(days: number): string {
  return new Date(Date.now() - days * DAY).toISOString();
}

function invoices(): CustomerInvoice[] {
  return [
    {
      id: 'inv-1',
      invoiceNumber: 'INV-7720',
      customerName: 'Emeka Nwosu',
      amountMinor: 11_890_000,
      currency: 'NGN',
      status: 'overdue',
      issuedAt: issuedDaysAgo(61),
      bookingReference: 'TRP-8K2P9A',
    },
    {
      id: 'inv-2',
      invoiceNumber: 'INV-7721',
      customerName: 'Ngozi Eze',
      amountMinor: 20_950_000,
      currency: 'NGN',
      status: 'pending',
      issuedAt: issuedDaysAgo(5),
      bookingReference: 'TRP-8K2L1E',
    },
    {
      id: 'inv-3',
      invoiceNumber: 'INV-7722',
      customerName: 'Folake Adeyemi',
      amountMinor: 38_200_000,
      currency: 'NGN',
      status: 'draft',
      issuedAt: issuedDaysAgo(1),
      bookingReference: 'TRP-8K2R6H',
    },
    {
      id: 'inv-4',
      invoiceNumber: 'INV-7723',
      customerName: 'Adaeze Okafor',
      amountMinor: 14_250_000,
      currency: 'NGN',
      status: 'paid',
      issuedAt: issuedDaysAgo(18),
      bookingReference: 'TRP-8K2Q4F',
    },
    {
      id: 'inv-5',
      invoiceNumber: 'INV-7724',
      customerName: 'Tunde Bakare',
      amountMinor: 8_700_000,
      currency: 'NGN',
      status: 'paid',
      issuedAt: issuedDaysAgo(40),
      bookingReference: 'TRP-8K2M7D',
    },
  ];
}

export const mockInvoicesApi: InvoicesApi = {
  async listInvoices() {
    await new Promise((resolve) => setTimeout(resolve, LATENCY_MS));
    return invoices();
  },
};
