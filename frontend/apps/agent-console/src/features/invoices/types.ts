/**
 * Invoices the agent has sent to their OWN travellers for bookings — not to
 * be confused with Billing, which is what Trips charges the agency for the
 * platform itself. There is no consolidated list of these anywhere else
 * today; the closest existing thing is the invoice PDF attached to a single
 * booking's detail page.
 *
 * Hand-written mock data for this first pass — a real backend read model for
 * this does not exist yet.
 */

export type InvoiceStatus = 'overdue' | 'pending' | 'draft' | 'paid';

export interface CustomerInvoice {
  id: string;
  invoiceNumber: string;
  customerName: string;
  /** What the traveller owes: net + the agent's markup + tax, as billed. */
  amountMinor: number;
  currency: string;
  status: InvoiceStatus;
  issuedAt: string;
  /** The booking this invoice was raised for, when there is one. */
  bookingReference: string | null;
}
