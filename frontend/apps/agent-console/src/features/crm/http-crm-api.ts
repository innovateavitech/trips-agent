import type { Schemas } from '@trips/api-client';
import { api } from '../../api/client';
import { unwrap } from '../../api/errors';
import { toOptionalWholeNumber, toWholeNumber } from '../pricing/pricing-rules';
import type { CrmApi } from './crm-api';
import type {
  Channel,
  Communication,
  Customer,
  CustomerBooking,
  CustomerSummary,
  Direction,
  Lead,
  LeadSource,
  LeadStage,
  LeadSummary,
  Quote,
  QuoteDay,
  QuoteItem,
  QuoteStatus,
  QuoteSummary,
  RelatedType,
  StageChange,
  Task,
} from './types';

/**
 * The CRM port over the real API (build plan F7, issue 162). The screens never
 * know which adapter they have: `main.tsx` chooses, and the stand-in in `mock/`
 * stays for demo mode.
 *
 * The API sends 64-bit numbers as `number | string` and enums as plain strings;
 * everything is turned into the console's own types here, once.
 */

function toCustomerRef(raw: Schemas['CustomerRefResponse']) {
  return { id: raw.id, name: raw.name, email: raw.email, phone: raw.phone };
}

function toLeadSummary(raw: Schemas['LeadSummaryResponse']): LeadSummary {
  return {
    id: raw.id,
    customer: toCustomerRef(raw.customer),
    source: raw.source as LeadSource,
    destination: raw.destination,
    travelFrom: raw.travelFrom,
    travelTo: raw.travelTo,
    adults: toWholeNumber(raw.adults),
    children: toWholeNumber(raw.children),
    budgetMaxMinor: toOptionalWholeNumber(raw.budgetMaxMinor),
    currency: raw.currency,
    stage: raw.stage as LeadStage,
    ownerName: raw.ownerName,
    createdAt: raw.createdAt,
    nextTaskDueAt: raw.nextTaskDueAt,
    quoteCount: toWholeNumber(raw.quoteCount),
  };
}

function toStageChange(raw: Schemas['StageChangeResponse']): StageChange {
  return { stage: raw.stage as LeadStage, at: raw.at, byName: raw.byName, reason: raw.reason };
}

function toTask(raw: Schemas['TaskResponse']): Task {
  return {
    id: raw.id,
    title: raw.title,
    dueAt: raw.dueAt,
    completedAt: raw.completedAt,
    related: {
      type: raw.related.type as RelatedType,
      id: raw.related.id,
      label: raw.related.label,
    },
    ownerName: raw.ownerName,
  };
}

function toCommunication(raw: Schemas['CommunicationResponse']): Communication {
  return {
    id: raw.id,
    channel: raw.channel as Channel,
    direction: raw.direction as Direction,
    summary: raw.summary,
    at: raw.at,
    byName: raw.byName,
    related: { type: raw.related.type as RelatedType, id: raw.related.id },
  };
}

function toQuoteSummary(raw: Schemas['QuoteSummaryResponse']): QuoteSummary {
  return {
    id: raw.id,
    quoteNumber: raw.quoteNumber,
    title: raw.title,
    status: raw.status as QuoteStatus,
    totalMinor: toWholeNumber(raw.totalMinor),
    currency: raw.currency,
    validUntil: raw.validUntil,
    sentAt: raw.sentAt,
  };
}

function toItem(raw: Schemas['QuoteItemResponse']): QuoteItem {
  return {
    description: raw.description,
    quantity: toWholeNumber(raw.quantity),
    unitPriceMinor: toWholeNumber(raw.unitPriceMinor),
    productId: raw.productId,
  };
}

function toDay(raw: Schemas['QuoteDayResponse']): QuoteDay {
  return {
    dayNumber: toWholeNumber(raw.dayNumber),
    title: raw.title,
    description: raw.description,
  };
}

export function toLead(raw: Schemas['LeadResponse']): Lead {
  return {
    ...toLeadSummary(raw),
    message: raw.message,
    budgetMinMinor: toOptionalWholeNumber(raw.budgetMinMinor),
    lostReason: raw.lostReason,
    history: raw.history.map(toStageChange),
    quotes: raw.quotes.map(toQuoteSummary),
    tasks: raw.tasks.map(toTask),
    communications: raw.communications.map(toCommunication),
  };
}

export function toQuote(raw: Schemas['QuoteResponse']): Quote {
  return {
    ...toQuoteSummary(raw),
    leadId: raw.leadId,
    customer: toCustomerRef(raw.customer),
    items: raw.items.map(toItem),
    itinerary: raw.itinerary.map(toDay),
    notes: raw.notes,
    publicUrl: raw.publicUrl,
    viewedAt: raw.viewedAt,
    respondedAt: raw.respondedAt,
  };
}

function toCustomerSummary(raw: Schemas['CustomerSummaryResponse']): CustomerSummary {
  return {
    id: raw.id,
    name: raw.name,
    email: raw.email,
    phone: raw.phone,
    lifetimeValueMinor: toWholeNumber(raw.lifetimeValueMinor),
    totalBookings: toWholeNumber(raw.totalBookings),
    lastActivityAt: raw.lastActivityAt,
    openLeadCount: toWholeNumber(raw.openLeadCount),
  };
}

function toBooking(raw: Schemas['CustomerBookingResponse']): CustomerBooking {
  return {
    reference: raw.reference,
    title: raw.title,
    travelDate: raw.travelDate,
    status: raw.status,
    amountMinor: toWholeNumber(raw.amountMinor),
  };
}

export function toCustomer(raw: Schemas['CustomerResponse']): Customer {
  return {
    ...toCustomerSummary(raw),
    currency: raw.currency,
    createdAt: raw.createdAt,
    leads: raw.leads.map(toLeadSummary),
    quotes: raw.quotes.map(toQuoteSummary),
    bookings: raw.bookings.map(toBooking),
    tasks: raw.tasks.map(toTask),
    communications: raw.communications.map(toCommunication),
  };
}

const byLead = (leadId: string) => ({ params: { path: { leadId } } });
const byQuote = (quoteId: string) => ({ params: { path: { quoteId } } });
const byTask = (taskId: string) => ({ params: { path: { taskId } } });

export const httpCrmApi: CrmApi = {
  async listLeads() {
    return (await unwrap(api.GET('/api/v1/crm/leads'))).map(toLeadSummary);
  },

  async getLead(id) {
    return toLead(await unwrap(api.GET('/api/v1/crm/leads/{leadId}', byLead(id))));
  },

  async createLead(request) {
    return toLead(await unwrap(api.POST('/api/v1/crm/leads', { body: request })));
  },

  async moveLead(id, stage, reason) {
    return toLead(
      await unwrap(
        api.POST('/api/v1/crm/leads/{leadId}/stage', { ...byLead(id), body: { stage, reason } }),
      ),
    );
  },

  async getQuote(id) {
    return toQuote(await unwrap(api.GET('/api/v1/crm/quotes/{quoteId}', byQuote(id))));
  },

  async createQuote(leadId, request) {
    return toQuote(
      await unwrap(
        api.POST('/api/v1/crm/leads/{leadId}/quotes', { ...byLead(leadId), body: request }),
      ),
    );
  },

  async saveQuote(id, request) {
    return toQuote(
      await unwrap(api.PUT('/api/v1/crm/quotes/{quoteId}', { ...byQuote(id), body: request })),
    );
  },

  async sendQuote(id) {
    return toQuote(await unwrap(api.POST('/api/v1/crm/quotes/{quoteId}/send', byQuote(id))));
  },

  async listCustomers() {
    return (await unwrap(api.GET('/api/v1/crm/customers'))).map(toCustomerSummary);
  },

  async getCustomer(id) {
    return toCustomer(
      await unwrap(
        api.GET('/api/v1/crm/customers/{customerId}', { params: { path: { customerId: id } } }),
      ),
    );
  },

  async listTasks({ open }) {
    return (await unwrap(api.GET('/api/v1/crm/tasks', { params: { query: { open } } }))).map(
      toTask,
    );
  },

  async addTask(request) {
    return toTask(await unwrap(api.POST('/api/v1/crm/tasks', { body: request })));
  },

  async completeTask(id) {
    return toTask(await unwrap(api.POST('/api/v1/crm/tasks/{taskId}/complete', byTask(id))));
  },

  async logCommunication(request) {
    return toCommunication(await unwrap(api.POST('/api/v1/crm/communications', { body: request })));
  },
};
