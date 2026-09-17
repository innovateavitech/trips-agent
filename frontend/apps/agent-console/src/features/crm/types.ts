/**
 * The CRM as the console sees it (build plan F7): leads from the storefront's
 * trip-request widget and elsewhere, the quotes that answer them, and the
 * customer record every inquiry, quote and booking adds to.
 *
 * Field for field the CRM contract in docs/BUILD_PLAN.md.
 */

import type { CustomerKind } from '../bookings/types';
import type { CustomerInvoice } from '../invoices/types';

export type { CustomerInvoice, CustomerKind };

export type LeadStage = 'New' | 'Quoted' | 'Negotiating' | 'Won' | 'Lost';
export type LeadSource = 'TripRequestWidget' | 'ContactForm' | 'Manual';
export type QuoteStatus = 'Draft' | 'Sent' | 'Viewed' | 'Accepted' | 'Declined' | 'Expired';
export type Channel = 'Email' | 'Sms' | 'Whatsapp' | 'Call' | 'Note';
export type Direction = 'Inbound' | 'Outbound';
export type RelatedType = 'Lead' | 'Customer' | 'Quote';

export interface CustomerRef {
  id: string;
  name: string;
  email: string | null;
  phone: string | null;
}

export interface LeadSummary {
  id: string;
  customer: CustomerRef;
  source: LeadSource;
  destination: string;
  /** `YYYY-MM-DD`. */
  travelFrom: string | null;
  travelTo: string | null;
  adults: number;
  children: number;
  budgetMaxMinor: number | null;
  currency: string;
  stage: LeadStage;
  ownerName: string | null;
  createdAt: string;
  nextTaskDueAt: string | null;
  quoteCount: number;
}

export interface StageChange {
  stage: LeadStage;
  at: string;
  byName: string;
  reason: string | null;
}

export interface Task {
  id: string;
  title: string;
  dueAt: string;
  completedAt: string | null;
  related: { type: RelatedType; id: string; label: string };
  ownerName: string | null;
}

export interface Communication {
  id: string;
  channel: Channel;
  direction: Direction;
  summary: string;
  at: string;
  byName: string;
  related: { type: RelatedType; id: string };
}

export interface QuoteSummary {
  id: string;
  quoteNumber: string;
  title: string;
  status: QuoteStatus;
  totalMinor: number;
  currency: string;
  /** `YYYY-MM-DD`. */
  validUntil: string;
  sentAt: string | null;
}

export interface Lead extends LeadSummary {
  message: string;
  budgetMinMinor: number | null;
  lostReason: string | null;
  history: StageChange[];
  quotes: QuoteSummary[];
  tasks: Task[];
  communications: Communication[];
}

/** A customer keyed in by the team from the Customers screen, before any booking. */
export interface CustomerRequest {
  kind: CustomerKind;
  name: string;
  email: string | null;
  phone: string | null;
}

export interface LeadRequest {
  customer: { name: string; email: string | null; phone: string | null };
  destination: string;
  travelFrom: string | null;
  travelTo: string | null;
  adults: number;
  children: number;
  budgetMinMinor: number | null;
  budgetMaxMinor: number | null;
  message: string;
}

export interface QuoteItem {
  description: string;
  quantity: number;
  unitPriceMinor: number;
  productId: string | null;
}

export interface QuoteDay {
  dayNumber: number;
  title: string;
  description: string;
}

export interface QuoteRequest {
  title: string;
  validUntil: string;
  items: QuoteItem[];
  itinerary: QuoteDay[];
  notes: string;
}

export interface Quote extends QuoteSummary {
  leadId: string;
  customer: CustomerRef;
  items: QuoteItem[];
  itinerary: QuoteDay[];
  notes: string;
  /** The customer's link, on the agency's own domain. Null until it is sent. */
  publicUrl: string | null;
  viewedAt: string | null;
  respondedAt: string | null;
}

export interface CustomerSummary {
  id: string;
  name: string;
  email: string | null;
  phone: string | null;
  lifetimeValueMinor: number;
  totalBookings: number;
  lastActivityAt: string;
  openLeadCount: number;
  /**
   * The three fields below are optional: the real customers endpoint (F7)
   * does not return them yet, so `createHttpCrmApi` leaves them unset and the
   * Customers screen shows "Individual" and "—" rather than guessing — the
   * same arrangement as `customerKind` on the Travel list.
   */
  kind?: CustomerKind;
  /** When they last booked anything, ISO 8601. Null when they never have. */
  lastBookingAt?: string | null;
  /** When the customer record was made, ISO 8601. */
  createdAt?: string;
}

export interface CustomerBooking {
  reference: string;
  title: string;
  travelDate: string | null;
  status: string;
  amountMinor: number;
  /**
   * The fields below are optional: the real customer endpoint (F7) does not
   * return them yet. Without them a row shows its `title` alone and counts
   * under "All" only — the screen never guesses a route or a carrier.
   */
  product?: 'flight' | 'bus';
  /** Cities for the row ("Lagos to Abuja") and codes for "Top route" ("LOS – ABV"). */
  route?: { from: string; to: string; fromCode: string; toCode: string };
  /** "Air Peace", "Libra Motors". */
  carrier?: string;
  /** When it was booked, ISO 8601. */
  bookedAt?: string;
}

/** Someone who travels on this customer's bookings — a business's staff, a family. */
export interface CustomerTraveller {
  id: string;
  title: 'Mr' | 'Mrs' | 'Ms' | 'Miss' | 'Dr' | null;
  name: string;
  email: string | null;
  /** `YYYY-MM-DD`. */
  dateOfBirth: string | null;
  gender: 'Male' | 'Female' | null;
}

export interface Customer extends CustomerSummary {
  currency: string;
  createdAt: string;
  leads: LeadSummary[];
  quotes: QuoteSummary[];
  bookings: CustomerBooking[];
  tasks: Task[];
  communications: Communication[];
  /** Optional until the customer endpoint returns them (F7): the tabs show their empty state. */
  invoices?: CustomerInvoice[];
  travellers?: CustomerTraveller[];
}

export interface TaskRequest {
  title: string;
  dueAt: string;
  related: { type: RelatedType; id: string };
}

export interface CommunicationRequest {
  channel: Channel;
  direction: Direction;
  summary: string;
  related: { type: RelatedType; id: string };
}
