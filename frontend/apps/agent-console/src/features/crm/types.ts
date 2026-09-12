/**
 * The CRM as the console sees it (build plan F7): leads from the storefront's
 * trip-request widget and elsewhere, the quotes that answer them, and the
 * customer record every inquiry, quote and booking adds to.
 *
 * Field for field the CRM contract in docs/BUILD_PLAN.md.
 */

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
}

export interface CustomerBooking {
  reference: string;
  title: string;
  travelDate: string | null;
  status: string;
  amountMinor: number;
}

export interface Customer extends CustomerSummary {
  currency: string;
  createdAt: string;
  leads: LeadSummary[];
  quotes: QuoteSummary[];
  bookings: CustomerBooking[];
  tasks: Task[];
  communications: Communication[];
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
