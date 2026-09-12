import { ApiError } from '../../../api/errors';
import type { CrmApi } from '../crm-api';
import { quoteTotalMinor, whyNotSendable } from '../crm-rules';
import type {
  Communication,
  Customer,
  CustomerRef,
  Lead,
  LeadSummary,
  Quote,
  QuoteSummary,
  Task,
} from '../types';
import { crmData, type StoredCustomer, type StoredLead } from './crm-store';

/**
 * ============================================================================
 *  TEMPORARY. Delete this folder when the CRM API lands (build plan F7).
 * ============================================================================
 *
 * The CRM screens' stand-in, keeping the contract's rules: a lost lead says
 * why, a sent quote is never edited, sending a quote moves a new lead to
 * Quoted, and a customer is created from the lead rather than keyed in first.
 */

const LATENCY_MS = 300;
const SAVE_LATENCY_MS = 500;

const delay = (ms = LATENCY_MS) => new Promise((resolve) => setTimeout(resolve, ms));
const today = () => new Date().toISOString().slice(0, 10);

let sequence = 100;
const nextId = (prefix: string) => `${prefix}-${(sequence += 1)}`;

function customerRef(id: string): CustomerRef {
  const found = crmData().customers.find((customer) => customer.id === id);
  if (!found) throw new ApiError(404, 'We could not find that customer.');
  return { id: found.id, name: found.name, email: found.email, phone: found.phone };
}

function quoteSummary(quote: Quote): QuoteSummary {
  const { id, quoteNumber, title, status, totalMinor, currency, validUntil, sentAt } = quote;
  return { id, quoteNumber, title, status, totalMinor, currency, validUntil, sentAt };
}

/** A sent quote past its date expires, as the server's nightly job would do. */
function withExpiry(quote: Quote): Quote {
  return (quote.status === 'Sent' || quote.status === 'Viewed') && quote.validUntil < today()
    ? { ...quote, status: 'Expired' }
    : quote;
}

function tasksFor(type: Task['related']['type'], id: string): Task[] {
  return crmData().tasks.filter((task) => task.related.type === type && task.related.id === id);
}

function summarise(lead: StoredLead): LeadSummary {
  const { quotes } = crmData();
  const related = [
    ...tasksFor('Lead', lead.id),
    ...quotes
      .filter((quote) => quote.leadId === lead.id)
      .flatMap((quote) => tasksFor('Quote', quote.id)),
  ];
  const nextTask = related
    .filter((task) => !task.completedAt)
    .sort((a, b) => a.dueAt.localeCompare(b.dueAt))[0];

  return {
    id: lead.id,
    customer: customerRef(lead.customerId),
    source: lead.source,
    destination: lead.destination,
    travelFrom: lead.travelFrom,
    travelTo: lead.travelTo,
    adults: lead.adults,
    children: lead.children,
    budgetMaxMinor: lead.budgetMaxMinor,
    currency: 'NGN',
    stage: lead.stage,
    ownerName: lead.ownerName,
    createdAt: lead.createdAt,
    nextTaskDueAt: nextTask?.dueAt ?? null,
    quoteCount: quotes.filter((quote) => quote.leadId === lead.id).length,
  };
}

function fullLead(lead: StoredLead): Lead {
  const { quotes, communications } = crmData();
  const leadQuotes = quotes.filter((quote) => quote.leadId === lead.id).map(withExpiry);

  return structuredClone({
    ...summarise(lead),
    message: lead.message,
    budgetMinMinor: lead.budgetMinMinor,
    lostReason: lead.lostReason,
    history: lead.history,
    quotes: leadQuotes.map(quoteSummary),
    tasks: [
      ...tasksFor('Lead', lead.id),
      ...leadQuotes.flatMap((quote) => tasksFor('Quote', quote.id)),
    ],
    communications: communications
      .filter((message) => message.related.type === 'Lead' && message.related.id === lead.id)
      .sort((a, b) => b.at.localeCompare(a.at)),
  });
}

function mustFindLead(id: string): StoredLead {
  const lead = crmData().leads.find((candidate) => candidate.id === id);
  if (!lead)
    throw new ApiError(404, 'We could not find that lead.', 'It may belong to another agency.');
  return lead;
}

function mustFindQuote(id: string): Quote {
  const quote = crmData().quotes.find((candidate) => candidate.id === id);
  if (!quote) throw new ApiError(404, 'We could not find that quote.');
  return quote;
}

function customerOf(customer: StoredCustomer): Customer {
  const { leads, quotes, communications } = crmData();
  const theirLeads = leads.filter((lead) => lead.customerId === customer.id);
  const leadIds = new Set(theirLeads.map((lead) => lead.id));
  const theirQuotes = quotes.filter((quote) => leadIds.has(quote.leadId)).map(withExpiry);
  const activity = [
    customer.createdAt,
    ...theirLeads.map((lead) => lead.createdAt),
    ...communications
      .filter((message) => leadIds.has(message.related.id))
      .map((message) => message.at),
  ].sort();

  return structuredClone({
    id: customer.id,
    name: customer.name,
    email: customer.email,
    phone: customer.phone,
    lifetimeValueMinor: customer.bookings.reduce((sum, booking) => sum + booking.amountMinor, 0),
    totalBookings: customer.bookings.length,
    lastActivityAt: activity[activity.length - 1] ?? customer.createdAt,
    openLeadCount: theirLeads.filter((lead) => lead.stage !== 'Won' && lead.stage !== 'Lost')
      .length,
    currency: 'NGN',
    createdAt: customer.createdAt,
    leads: theirLeads.map(summarise),
    quotes: theirQuotes.map(quoteSummary),
    bookings: customer.bookings,
    tasks: [
      ...tasksFor('Customer', customer.id),
      ...theirLeads.flatMap((lead) => tasksFor('Lead', lead.id)),
    ],
    communications: communications
      .filter((message) => leadIds.has(message.related.id) || message.related.id === customer.id)
      .sort((a, b) => b.at.localeCompare(a.at)),
  });
}

function relatedLabel(type: Task['related']['type'], id: string): string {
  if (type === 'Lead') {
    const lead = mustFindLead(id);
    return `${customerRef(lead.customerId).name} · ${lead.destination}`;
  }
  if (type === 'Quote') {
    const quote = mustFindQuote(id);
    return `${quote.quoteNumber} · ${quote.customer.name}`;
  }
  return customerRef(id).name;
}

export const mockCrmApi: CrmApi = {
  async listLeads() {
    await delay();
    return crmData().leads.map(summarise);
  },

  async getLead(id) {
    await delay();
    return fullLead(mustFindLead(id));
  },

  async createLead(request) {
    await delay(SAVE_LATENCY_MS);
    const { customers, leads } = crmData();

    if (!request.customer.name.trim()) throw new ApiError(422, 'Give the customer a name.');
    if (!request.destination.trim()) throw new ApiError(422, 'Say where they want to go.');

    // Customers are never keyed in first: find them by email or phone, or create them from the lead.
    const email = request.customer.email?.trim().toLowerCase() || null;
    const phone = request.customer.phone?.replace(/\s+/g, '') || null;
    let customer = customers.find(
      (candidate) =>
        (email && candidate.email?.toLowerCase() === email) ||
        (phone && candidate.phone?.replace(/\s+/g, '') === phone),
    );
    if (!customer) {
      customer = {
        id: nextId('c'),
        name: request.customer.name.trim(),
        email: request.customer.email?.trim() || null,
        phone: request.customer.phone?.trim() || null,
        createdAt: new Date().toISOString(),
        bookings: [],
      };
      customers.push(customer);
    }

    const now = new Date().toISOString();
    const lead: StoredLead = {
      id: nextId('l'),
      customerId: customer.id,
      source: 'Manual',
      destination: request.destination.trim(),
      travelFrom: request.travelFrom,
      travelTo: request.travelTo,
      adults: request.adults,
      children: request.children,
      budgetMinMinor: request.budgetMinMinor,
      budgetMaxMinor: request.budgetMaxMinor,
      message: request.message.trim(),
      stage: 'New',
      lostReason: null,
      ownerName: 'Owner',
      createdAt: now,
      history: [{ stage: 'New', at: now, byName: 'Owner', reason: null }],
    };
    leads.push(lead);
    return fullLead(lead);
  },

  async moveLead(id, stage, reason) {
    await delay(SAVE_LATENCY_MS);
    const lead = mustFindLead(id);

    if (lead.stage === stage)
      throw new ApiError(409, `This lead is already ${stage.toLowerCase()}.`);
    if (stage === 'Lost' && !reason?.trim()) {
      throw new ApiError(
        422,
        'Say why it was lost.',
        'It is how the agency learns what to change.',
      );
    }

    lead.stage = stage;
    lead.lostReason = stage === 'Lost' ? reason!.trim() : null;
    lead.history.push({
      stage,
      at: new Date().toISOString(),
      byName: 'Owner',
      reason: stage === 'Lost' ? reason!.trim() : null,
    });
    return fullLead(lead);
  },

  async getQuote(id) {
    await delay();
    return structuredClone(withExpiry(mustFindQuote(id)));
  },

  async createQuote(leadId, request) {
    await delay(SAVE_LATENCY_MS);
    const lead = mustFindLead(leadId);
    const { quotes } = crmData();

    const quote: Quote = {
      id: nextId('q'),
      quoteNumber: `QT-${String(quotes.length + 9).padStart(4, '0')}`,
      leadId,
      customer: customerRef(lead.customerId),
      ...structuredClone(request),
      status: 'Draft',
      currency: 'NGN',
      totalMinor: quoteTotalMinor(request.items),
      publicUrl: null,
      sentAt: null,
      viewedAt: null,
      respondedAt: null,
    };
    quotes.push(quote);
    return structuredClone(quote);
  },

  async saveQuote(id, request) {
    await delay(SAVE_LATENCY_MS);
    const quote = mustFindQuote(id);

    if (quote.status !== 'Draft') {
      throw new ApiError(
        409,
        'A sent quote cannot be changed.',
        'The customer has it as it was sent. Make a new quote for new terms.',
      );
    }

    Object.assign(quote, structuredClone(request), { totalMinor: quoteTotalMinor(request.items) });
    return structuredClone(quote);
  },

  async sendQuote(id) {
    await delay(SAVE_LATENCY_MS);
    const quote = mustFindQuote(id);
    const problem = whyNotSendable(quote, today());
    if (problem) throw new ApiError(409, 'This quote cannot be sent yet.', problem);

    const now = new Date().toISOString();
    const token = Math.random().toString(36).slice(2, 7);
    Object.assign(quote, {
      status: 'Sent',
      sentAt: now,
      publicUrl: `https://lekki-horizon.example/q/${token}`,
    });

    const lead = mustFindLead(quote.leadId);
    if (lead.stage === 'New') {
      lead.stage = 'Quoted';
      lead.history.push({ stage: 'Quoted', at: now, byName: 'Owner', reason: null });
    }
    crmData().communications.push({
      id: nextId('m'),
      channel: 'Email',
      direction: 'Outbound',
      summary: `Sent quote ${quote.quoteNumber}.`,
      at: now,
      byName: 'Owner',
      related: { type: 'Lead', id: lead.id },
    });

    return structuredClone(quote);
  },

  async listCustomers() {
    await delay();
    return crmData().customers.map((customer) => {
      const {
        leads: _leads,
        quotes: _quotes,
        bookings: _bookings,
        tasks: _tasks,
        communications: _messages,
        currency: _currency,
        createdAt: _createdAt,
        ...summary
      } = customerOf(customer);
      return summary;
    });
  },

  async getCustomer(id) {
    await delay();
    const customer = crmData().customers.find((candidate) => candidate.id === id);
    if (!customer) throw new ApiError(404, 'We could not find that customer.');
    return customerOf(customer);
  },

  async listTasks({ open }) {
    await delay();
    return structuredClone(crmData().tasks.filter((task) => (open ? !task.completedAt : true)));
  },

  async addTask(request) {
    await delay(SAVE_LATENCY_MS);
    if (!request.title.trim()) throw new ApiError(422, 'Say what needs doing.');

    const task: Task = {
      id: nextId('t'),
      title: request.title.trim(),
      dueAt: request.dueAt,
      completedAt: null,
      related: {
        ...request.related,
        label: relatedLabel(request.related.type, request.related.id),
      },
      ownerName: 'Owner',
    };
    crmData().tasks.push(task);
    return structuredClone(task);
  },

  async completeTask(id) {
    await delay(SAVE_LATENCY_MS);
    const task = crmData().tasks.find((candidate) => candidate.id === id);
    if (!task) throw new ApiError(404, 'We could not find that task.');
    if (task.completedAt) throw new ApiError(409, 'That task is already done.');

    task.completedAt = new Date().toISOString();
    return structuredClone(task);
  },

  async logCommunication(request) {
    await delay(SAVE_LATENCY_MS);
    if (!request.summary.trim()) throw new ApiError(422, 'Say what was said.');

    const message: Communication = {
      id: nextId('m'),
      ...request,
      summary: request.summary.trim(),
      at: new Date().toISOString(),
      byName: 'Owner',
    };
    crmData().communications.push(message);
    return structuredClone(message);
  },
};
