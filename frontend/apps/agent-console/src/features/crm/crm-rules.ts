import { formatMoneyShort } from '@trips/utils';
import { amountInputFromMinor, parseAmount } from '../pricing/pricing-rules';
import type {
  Channel,
  LeadSource,
  LeadStage,
  LeadSummary,
  Quote,
  QuoteItem,
  QuoteRequest,
  QuoteStatus,
  Task,
} from './types';

/**
 * Everything the CRM screens decide, as plain functions — tested in
 * `__tests__/crm-rules.test.ts`.
 */

export type Problems = Record<string, string>;

/* ---------------------------------------------------------------- stages -- */

export const STAGES: ReadonlyArray<{ value: LeadStage; label: string; hint: string }> = [
  { value: 'New', label: 'New', hint: 'Waiting for a first answer' },
  { value: 'Quoted', label: 'Quoted', hint: 'A quote has gone out' },
  { value: 'Negotiating', label: 'Negotiating', hint: 'Talking it through' },
  { value: 'Won', label: 'Won', hint: 'Booked' },
  { value: 'Lost', label: 'Lost', hint: 'Went elsewhere, or not now' },
];

export const STAGE_TONE = {
  New: 'info',
  Quoted: 'primary',
  Negotiating: 'warning',
  Won: 'success',
  Lost: 'neutral',
} as const satisfies Record<LeadStage, string>;

export const SOURCE_LABEL: Record<LeadSource, string> = {
  TripRequestWidget: 'Website trip request',
  ContactForm: 'Contact form',
  Manual: 'Added by the team',
};

export const CHANNEL_LABEL: Record<Channel, string> = {
  Email: 'Email',
  Sms: 'SMS',
  Whatsapp: 'WhatsApp',
  Call: 'Call',
  Note: 'Note',
};

/** The board's columns, newest lead first in each. */
export function groupByStage(leads: readonly LeadSummary[]): Record<LeadStage, LeadSummary[]> {
  const columns: Record<LeadStage, LeadSummary[]> = {
    New: [],
    Quoted: [],
    Negotiating: [],
    Won: [],
    Lost: [],
  };
  for (const lead of [...leads].sort((a, b) => b.createdAt.localeCompare(a.createdAt))) {
    columns[lead.stage].push(lead);
  }
  return columns;
}

export function searchLeads(leads: readonly LeadSummary[], query: string): LeadSummary[] {
  const needle = query.trim().toLowerCase();
  if (!needle) return [...leads];
  return leads.filter((lead) =>
    [
      lead.customer.name,
      lead.customer.email ?? '',
      lead.customer.phone ?? '',
      lead.destination,
    ].some((text) => text.toLowerCase().includes(needle)),
  );
}

/* -------------------------------------------------------------- describe -- */

const shortDay = new Intl.DateTimeFormat('en-NG', {
  day: 'numeric',
  month: 'short',
  timeZone: 'Africa/Lagos',
});

const asDate = (date: string) => new Date(`${date}T12:00:00+01:00`);

/** "12–19 Dec", "12 Dec", or "" when there are no dates. */
export function describeDates(from: string | null, to: string | null): string {
  if (!from) return '';
  if (!to || to === from) return shortDay.format(asDate(from));
  const [fromDay, fromMonth] = shortDay.format(asDate(from)).split(' ');
  const [toDay, toMonth] = shortDay.format(asDate(to)).split(' ');
  return fromMonth === toMonth
    ? `${fromDay}–${toDay} ${toMonth}`
    : `${fromDay} ${fromMonth} – ${toDay} ${toMonth}`;
}

export function describeParty(adults: number, children: number): string {
  const parts = [`${adults} ${adults === 1 ? 'adult' : 'adults'}`];
  if (children > 0) parts.push(`${children} ${children === 1 ? 'child' : 'children'}`);
  return parts.join(', ');
}

/** "Zanzibar · 20–27 Nov · 2 adults". */
export function describeTrip(
  lead: Pick<LeadSummary, 'destination' | 'travelFrom' | 'travelTo' | 'adults' | 'children'>,
): string {
  return [
    lead.destination,
    describeDates(lead.travelFrom, lead.travelTo),
    describeParty(lead.adults, lead.children),
  ]
    .filter(Boolean)
    .join(' · ');
}

/** "₦3,000,000 to ₦4,000,000", "Up to ₦4,500,000", or "No budget given". */
export function describeBudget(
  minMinor: number | null,
  maxMinor: number | null,
  currency: string,
): string {
  const money = (amount: number) => formatMoneyShort(amount, currency);
  if (minMinor !== null && maxMinor !== null) return `${money(minMinor)} to ${money(maxMinor)}`;
  if (maxMinor !== null) return `Up to ${money(maxMinor)}`;
  if (minMinor !== null) return `From ${money(minMinor)}`;
  return 'No budget given';
}

/** "just now", "3 hours ago", "in 2 days" — how long ago, or how long until. */
export function relativeTime(iso: string, now: Date = new Date()): string {
  const minutes = Math.round((new Date(iso).getTime() - now.getTime()) / 60_000);
  const past = minutes < 0;
  const size = Math.abs(minutes);

  const [amount, unit] =
    size < 1
      ? [0, 'minute']
      : size < 60
        ? [size, 'minute']
        : size < 60 * 24
          ? [Math.round(size / 60), 'hour']
          : [Math.round(size / (60 * 24)), 'day'];

  if (amount === 0) return 'just now';
  const phrase = `${amount} ${unit}${amount === 1 ? '' : 's'}`;
  return past ? `${phrase} ago` : `in ${phrase}`;
}

/* ----------------------------------------------------------------- tasks -- */

export type TaskBucket = 'overdue' | 'today' | 'upcoming' | 'done';

const lagosDay = (iso: string) =>
  new Intl.DateTimeFormat('en-CA', { timeZone: 'Africa/Lagos' }).format(new Date(iso));

/** Which list a task belongs in, by Lagos days: overdue, due today, later, or done. */
export function bucketOf(
  task: Pick<Task, 'dueAt' | 'completedAt'>,
  now: Date = new Date(),
): TaskBucket {
  if (task.completedAt) return 'done';
  if (new Date(task.dueAt).getTime() < now.getTime()) return 'overdue';
  return lagosDay(task.dueAt) === lagosDay(now.toISOString()) ? 'today' : 'upcoming';
}

export function bucketTasks(
  tasks: readonly Task[],
  now: Date = new Date(),
): Record<TaskBucket, Task[]> {
  const buckets: Record<TaskBucket, Task[]> = { overdue: [], today: [], upcoming: [], done: [] };
  for (const task of [...tasks].sort((a, b) => a.dueAt.localeCompare(b.dueAt)))
    buckets[bucketOf(task, now)].push(task);
  return buckets;
}

/* ---------------------------------------------------------------- quotes -- */

export const QUOTE_STATUS_TONE = {
  Draft: 'neutral',
  Sent: 'info',
  Viewed: 'primary',
  Accepted: 'success',
  Declined: 'destructive',
  Expired: 'warning',
} as const satisfies Record<QuoteStatus, string>;

export function quoteTotalMinor(
  items: readonly Pick<QuoteItem, 'quantity' | 'unitPriceMinor'>[],
): number {
  return items.reduce((sum, item) => sum + item.quantity * item.unitPriceMinor, 0);
}

/** Why a quote cannot go to the customer yet — or null when it can. */
export function whyNotSendable(
  quote: Pick<Quote, 'status' | 'items' | 'validUntil'>,
  today: string,
): string | null {
  if (quote.status !== 'Draft') return 'It has already been sent.';
  if (quote.items.length === 0) return 'Add at least one item.';
  if (quoteTotalMinor(quote.items) <= 0) return 'The total is nothing yet.';
  if (quote.validUntil < today) return 'It expired before it was sent. Move the date.';
  return null;
}

/** The quote builder's working copy, as typed. */
export interface ItemDraft {
  key: string;
  description: string;
  quantity: string;
  unitPrice: string;
}

export interface DayDraft {
  key: string;
  title: string;
  description: string;
}

export interface QuoteDraft {
  title: string;
  validUntil: string;
  items: ItemDraft[];
  itinerary: DayDraft[];
  notes: string;
}

let keyCounter = 0;

export function newCrmKey(): string {
  keyCounter += 1;
  return `crm-row-${keyCounter}`;
}

export function emptyItem(): ItemDraft {
  return { key: newCrmKey(), description: '', quantity: '1', unitPrice: '' };
}

export function emptyQuoteDay(): DayDraft {
  return { key: newCrmKey(), title: '', description: '' };
}

/** A fresh quote for a lead: named after the trip, valid for a week. */
export function emptyQuoteDraft(
  lead: Pick<LeadSummary, 'destination' | 'adults' | 'children'>,
  today: string,
  plusDays: (date: string, days: number) => string,
): QuoteDraft {
  return {
    title: `${lead.destination} for ${describeParty(lead.adults, lead.children)}`,
    validUntil: plusDays(today, 7),
    items: [emptyItem()],
    itinerary: [],
    notes: '',
  };
}

export function draftFromQuote(
  quote: Pick<Quote, 'title' | 'validUntil' | 'items' | 'itinerary' | 'notes'>,
): QuoteDraft {
  return {
    title: quote.title,
    validUntil: quote.validUntil,
    items: quote.items.map((item) => ({
      key: newCrmKey(),
      description: item.description,
      quantity: String(item.quantity),
      unitPrice: amountInputFromMinor(item.unitPriceMinor),
    })),
    itinerary: quote.itinerary.map((day) => ({
      key: newCrmKey(),
      title: day.title,
      description: day.description,
    })),
    notes: quote.notes,
  };
}

export type BuiltQuote = { ok: true; request: QuoteRequest } | { ok: false; errors: Problems };

/** The draft as the API takes it — every problem at once, keyed by field. */
export function buildQuoteRequest(draft: QuoteDraft, today: string): BuiltQuote {
  const errors: Problems = {};

  if (!draft.title.trim()) errors['title'] = 'Give the quote a title the customer will recognise.';
  if (!/^\d{4}-\d{2}-\d{2}$/.test(draft.validUntil))
    errors['validUntil'] = 'Choose the last day it can be accepted.';
  else if (draft.validUntil < today) errors['validUntil'] = 'That day has passed.';

  const items = draft.items
    .map((item, index) => ({ item, index }))
    .filter(({ item }) => item.description.trim() || item.unitPrice.trim())
    .map(({ item, index }): QuoteItem => {
      if (!item.description.trim()) errors[`items.${index}.description`] = 'Say what it is.';
      const quantity = item.quantity.trim();
      if (!/^\d+$/.test(quantity) || Number(quantity) < 1)
        errors[`items.${index}.quantity`] = 'One or more.';
      const price = parseAmount(item.unitPrice);
      if (!price.ok) errors[`items.${index}.unitPrice`] = price.error;
      return {
        description: item.description.trim(),
        quantity: /^\d+$/.test(quantity) ? Number(quantity) : 0,
        unitPriceMinor: price.ok ? price.value : 0,
        productId: null,
      };
    });

  if (items.length === 0) errors['items'] = 'Add at least one item.';

  const request: QuoteRequest = {
    title: draft.title.trim(),
    validUntil: draft.validUntil,
    items,
    itinerary: draft.itinerary
      .filter((day) => day.title.trim() || day.description.trim())
      .map((day, index) => ({
        dayNumber: index + 1,
        title: day.title.trim(),
        description: day.description.trim(),
      })),
    notes: draft.notes.trim(),
  };

  return Object.keys(errors).length > 0 ? { ok: false, errors } : { ok: true, request };
}
