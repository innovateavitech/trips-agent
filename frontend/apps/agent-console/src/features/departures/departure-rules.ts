import {
  amountInputFromMinor,
  formatPercent,
  parseAmount,
  parsePercent,
} from '../pricing/pricing-rules';
import type {
  Departure,
  DepartureRequest,
  DepartureStatus,
  DepositType,
  DueBasis,
  PriceTier,
} from './types';

/**
 * Everything the departure screens decide, as plain functions — tested in
 * `__tests__/departure-rules.test.ts`. The status itself is not decided here:
 * the server works it out from the seats, and the screens show it.
 */

/** Field key → what is wrong there, in words the agent can act on. */
export type Problems = Record<string, string>;

const FULL = 10_000; // 100%, in basis points

/* ---------------------------------------------------------------- status -- */

export const STATUS_LABEL: Record<DepartureStatus, string> = {
  Open: 'Open',
  Guaranteed: 'Guaranteed to run',
  NearlyFull: 'Nearly full',
  SoldOut: 'Sold out',
  Closed: 'Closed',
  Cancelled: 'Cancelled',
};

export const STATUS_TONE = {
  Open: 'info',
  Guaranteed: 'success',
  NearlyFull: 'warning',
  SoldOut: 'primary',
  Closed: 'neutral',
  Cancelled: 'destructive',
} as const satisfies Record<DepartureStatus, string>;

export type StatusGroup = 'all' | 'selling' | 'soldOut' | 'closed';

export const STATUS_GROUPS: ReadonlyArray<{ value: StatusGroup; label: string }> = [
  { value: 'all', label: 'All' },
  { value: 'selling', label: 'Selling' },
  { value: 'soldOut', label: 'Sold out' },
  { value: 'closed', label: 'Closed or cancelled' },
];

export function inGroup(status: DepartureStatus, group: StatusGroup): boolean {
  switch (group) {
    case 'all':
      return true;
    case 'selling':
      return status === 'Open' || status === 'Guaranteed' || status === 'NearlyFull';
    case 'soldOut':
      return status === 'SoldOut';
    case 'closed':
      return status === 'Closed' || status === 'Cancelled';
  }
}

export function groupCounts(
  departures: readonly Pick<Departure, 'status'>[],
): Record<StatusGroup, number> {
  const counts: Record<StatusGroup, number> = { all: 0, selling: 0, soldOut: 0, closed: 0 };
  for (const departure of departures) {
    for (const group of STATUS_GROUPS)
      if (inGroup(departure.status, group.value)) counts[group.value] += 1;
  }
  return counts;
}

/* ----------------------------------------------------------------- seats -- */

type Seats = Pick<Departure, 'capacityTotal' | 'capacityConfirmed' | 'capacityReserved'>;

export function seatsTaken(departure: Seats): number {
  return departure.capacityConfirmed + departure.capacityReserved;
}

/** "11 of 16 taken · 2 held in checkout". */
export function describeSeats(departure: Seats): string {
  const taken = `${seatsTaken(departure)} of ${departure.capacityTotal} taken`;
  return departure.capacityReserved > 0
    ? `${taken} · ${departure.capacityReserved} held in checkout`
    : taken;
}

/** Whether a group departure will run, in the words an agent uses with a customer. Null otherwise. */
export function describeGuarantee(
  departure: Pick<Departure, 'isGroupDeparture' | 'minPax' | 'capacityConfirmed'>,
): string | null {
  if (!departure.isGroupDeparture) return null;
  const missing = departure.minPax - departure.capacityConfirmed;
  return missing <= 0
    ? `It runs: ${departure.capacityConfirmed} have paid, and ${departure.minPax} were needed.`
    : `Needs ${missing} more paid ${missing === 1 ? 'traveller' : 'travellers'} to run.`;
}

/* ----------------------------------------------------------------- price -- */

export function lowestPriceMinor(tiers: readonly PriceTier[]): number | null {
  return tiers.length === 0 ? null : Math.min(...tiers.map((tier) => tier.pricePerPaxMinor));
}

/** The price per traveller for a party of this size, or null when no tier covers it. */
export function priceForParty(tiers: readonly PriceTier[], pax: number): number | null {
  const tier = tiers.find((t) => pax >= t.minPax && (t.maxPax === null || pax <= t.maxPax));
  return tier?.pricePerPaxMinor ?? null;
}

/* ----------------------------------------------------------------- dates -- */

/** A `YYYY-MM-DD` some whole days away. Date-only arithmetic, so no clock change can shift it. */
export function plusDays(date: string, days: number): string {
  const [year = 0, month = 1, day = 1] = date.split('-').map(Number);
  return new Date(Date.UTC(year, month - 1, day + days)).toISOString().slice(0, 10);
}

/** Today in Lagos, `YYYY-MM-DD` — the day an agent means by "today". */
export function todayInLagos(now: Date = new Date()): string {
  return new Intl.DateTimeFormat('en-CA', { timeZone: 'Africa/Lagos' }).format(now);
}

const dayFormat = new Intl.DateTimeFormat('en-NG', {
  weekday: 'short',
  day: 'numeric',
  month: 'short',
  year: 'numeric',
  timeZone: 'Africa/Lagos',
});

/** `2026-10-26` → "Mon, 26 Oct 2026". */
export function formatDay(date: string): string {
  return /^\d{4}-\d{2}-\d{2}$/.test(date)
    ? dayFormat.format(new Date(`${date}T12:00:00+01:00`))
    : '';
}

/* ------------------------------------------------------------ validation -- */

/**
 * The contract's rules for a departure, applied to what would be sent. Every
 * problem at once, keyed by the field it is about; the server applies the same.
 */
export function validateDeparture(request: DepartureRequest, today: string): Problems {
  const problems: Problems = {};
  const wholeAtLeast = (value: number, min: number) => Number.isInteger(value) && value >= min;

  if (!/^\d{4}-\d{2}-\d{2}$/.test(request.departureDate)) {
    problems['departureDate'] = 'Choose the day it leaves.';
  } else if (request.departureDate <= today) {
    problems['departureDate'] = 'A departure has to be in the future.';
  }

  if (!wholeAtLeast(request.capacityTotal, 1)) problems['capacityTotal'] = 'At least one seat.';

  if (request.isGroupDeparture) {
    if (!wholeAtLeast(request.minPax, 1)) problems['minPax'] = 'At least one traveller.';
    else if (request.minPax > request.capacityTotal)
      problems['minPax'] = 'More than the seats there are.';
  }

  if (!wholeAtLeast(request.cutoffDaysBefore, 0))
    problems['cutoffDaysBefore'] = 'Zero or more days.';

  if (request.priceTiers.length === 0) problems['priceTiers'] = 'Set a price per traveller.';
  request.priceTiers.forEach((tier, index) => {
    const previous = request.priceTiers[index - 1];
    const expectedMin =
      index === 0 ? 1 : previous?.maxPax === null ? Number.NaN : (previous?.maxPax ?? 0) + 1;
    if (tier.minPax !== expectedMin) {
      problems['priceTiers'] = 'Party sizes have to follow on from 1, with no gaps or overlaps.';
    }
    if (tier.maxPax === null && index < request.priceTiers.length - 1) {
      problems[`priceTiers.${index}.maxPax`] = 'Only the last party size can be open-ended.';
    } else if (tier.maxPax !== null && tier.maxPax < tier.minPax) {
      problems[`priceTiers.${index}.maxPax`] = `At least ${tier.minPax}.`;
    }
    if (tier.pricePerPaxMinor <= 0) problems[`priceTiers.${index}.price`] = 'Set a price.';
  });

  if (request.depositType === 'Percent') {
    const share = request.depositPercentBasisPoints ?? 0;
    if (share <= 0 || share > FULL) problems['deposit'] = 'More than 0%, and at most 100%.';
  } else if (request.depositType === 'Fixed') {
    const amount = request.depositAmountMinor ?? 0;
    const lowest = lowestPriceMinor(request.priceTiers);
    if (amount <= 0) problems['deposit'] = 'Set the deposit amount.';
    else if (lowest !== null && amount > lowest)
      problems['deposit'] = 'More than the price of a seat.';
  }

  if (request.installments.length > 0) {
    const total = request.installments.reduce(
      (sum, item) => sum + item.percentOfBalanceBasisPoints,
      0,
    );
    if (total !== FULL) {
      problems['installments'] =
        `The payments add up to ${formatPercent(total)}% of the balance, not 100%.`;
    }
    request.installments.forEach((item, index) => {
      if (!wholeAtLeast(item.dueOffsetDays, 0))
        problems[`installments.${index}.offset`] = 'Zero or more days.';
      if (item.percentOfBalanceBasisPoints <= 0)
        problems[`installments.${index}.share`] = 'More than 0%.';
    });
  }

  return problems;
}

/* ------------------------------------------------------------------ form -- */

/**
 * The form's working copy, as typed. A party size's lower bound is never
 * typed: it is one more than the size before it, so sizes cannot overlap or
 * leave a gap however they are edited.
 */
export interface TierDraft {
  key: string;
  maxPax: string;
  price: string;
}

export interface InstallmentDraft {
  key: string;
  dueBasis: DueBasis;
  offsetDays: string;
  share: string;
}

export interface DepartureDraft {
  departureDate: string;
  isGroupDeparture: boolean;
  minPax: string;
  capacityTotal: string;
  cutoffDaysBefore: string;
  depositType: DepositType;
  depositPercent: string;
  depositAmount: string;
  tiers: TierDraft[];
  installments: InstallmentDraft[];
}

let keyCounter = 0;

export function newRowKey(): string {
  keyCounter += 1;
  return `departure-row-${keyCounter}`;
}

export function emptyTier(): TierDraft {
  return { key: newRowKey(), maxPax: '', price: '' };
}

export function emptyInstallment(): InstallmentDraft {
  return { key: newRowKey(), dueBasis: 'BeforeDeparture', offsetDays: '30', share: '' };
}

export function emptyDepartureDraft(basePriceMinor: number): DepartureDraft {
  return {
    departureDate: '',
    isGroupDeparture: true,
    minPax: '6',
    capacityTotal: '16',
    cutoffDaysBefore: '14',
    depositType: 'Percent',
    depositPercent: '30',
    depositAmount: '',
    tiers: [
      { ...emptyTier(), price: basePriceMinor > 0 ? amountInputFromMinor(basePriceMinor) : '' },
    ],
    installments: [],
  };
}

export function draftFromDeparture(departure: DepartureRequest): DepartureDraft {
  return {
    departureDate: departure.departureDate,
    isGroupDeparture: departure.isGroupDeparture,
    minPax: String(departure.minPax),
    capacityTotal: String(departure.capacityTotal),
    cutoffDaysBefore: String(departure.cutoffDaysBefore),
    depositType: departure.depositType,
    depositPercent:
      departure.depositPercentBasisPoints === null
        ? ''
        : formatPercent(departure.depositPercentBasisPoints),
    depositAmount:
      departure.depositAmountMinor === null
        ? ''
        : amountInputFromMinor(departure.depositAmountMinor),
    tiers: departure.priceTiers.map((tier) => ({
      key: newRowKey(),
      maxPax: tier.maxPax === null ? '' : String(tier.maxPax),
      price: amountInputFromMinor(tier.pricePerPaxMinor),
    })),
    installments: departure.installments.map((item) => ({
      key: newRowKey(),
      dueBasis: item.dueBasis,
      offsetDays: String(item.dueOffsetDays),
      share: formatPercent(item.percentOfBalanceBasisPoints),
    })),
  };
}

/** Each party size's lower bound: 1, then one more than the size before. NaN while that is unreadable. */
export function tierMins(tiers: readonly Pick<TierDraft, 'maxPax'>[]): number[] {
  const mins: number[] = [];
  tiers.forEach((_, index) => {
    if (index === 0) {
      mins.push(1);
      return;
    }
    const previous = tiers[index - 1]?.maxPax.trim() ?? '';
    mins.push(/^\d+$/.test(previous) ? Number(previous) + 1 : Number.NaN);
  });
  return mins;
}

export type BuiltDeparture =
  { ok: true; request: DepartureRequest } | { ok: false; errors: Problems };

/** The draft as the API takes it — or what could not be read, field by field. */
export function buildDepartureRequest(draft: DepartureDraft): BuiltDeparture {
  const errors: Problems = {};

  const whole = (field: string, input: string): number => {
    const trimmed = input.trim();
    if (/^\d+$/.test(trimmed)) return Number(trimmed);
    errors[field] = trimmed ? 'A whole number.' : 'Enter a number.';
    return 0;
  };
  const money = (field: string, input: string): number => {
    const parsed = parseAmount(input);
    if (parsed.ok) return parsed.value;
    errors[field] = parsed.error;
    return 0;
  };
  const share = (field: string, input: string): number => {
    const parsed = parsePercent(input);
    if (!parsed.ok) {
      errors[field] = parsed.error;
      return 0;
    }
    if (parsed.value > FULL) errors[field] = 'At most 100%.';
    return parsed.value;
  };

  const mins = tierMins(draft.tiers);
  const priceTiers: PriceTier[] = draft.tiers.map((tier, index) => {
    const maxText = tier.maxPax.trim();
    return {
      minPax: mins[index] ?? Number.NaN,
      maxPax: maxText ? whole(`priceTiers.${index}.maxPax`, maxText) : null,
      pricePerPaxMinor: money(`priceTiers.${index}.price`, tier.price),
    };
  });

  const request: DepartureRequest = {
    departureDate: draft.departureDate,
    isGroupDeparture: draft.isGroupDeparture,
    minPax: draft.isGroupDeparture ? whole('minPax', draft.minPax) : 1,
    capacityTotal: whole('capacityTotal', draft.capacityTotal),
    cutoffDaysBefore: whole('cutoffDaysBefore', draft.cutoffDaysBefore),
    depositType: draft.depositType,
    depositPercentBasisPoints:
      draft.depositType === 'Percent' ? share('deposit', draft.depositPercent) : null,
    depositAmountMinor:
      draft.depositType === 'Fixed' ? money('deposit', draft.depositAmount) : null,
    priceTiers,
    installments: draft.installments.map((item, index) => ({
      sequence: index + 1,
      dueBasis: item.dueBasis,
      dueOffsetDays: whole(`installments.${index}.offset`, item.offsetDays),
      percentOfBalanceBasisPoints: share(`installments.${index}.share`, item.share),
    })),
  };

  return Object.keys(errors).length > 0 ? { ok: false, errors } : { ok: true, request };
}

/* -------------------------------------------------------------- schedule -- */

export interface ScheduleLine {
  label: string;
  dueDate: string;
  amountMinor: number;
  /** Due the day they book — its date has already passed, or it is the deposit. */
  dueNow: boolean;
}

/**
 * What one traveller pays and when, if they book on `bookingDate` at this
 * price. Whole kobo throughout: each payment is rounded down and the last one
 * takes what is left, so the lines always add up to the price exactly.
 */
export function paymentSchedule(
  request: DepartureRequest,
  bookingDate: string,
  pricePerPaxMinor: number,
): ScheduleLine[] {
  const deposit =
    request.depositType === 'Percent'
      ? Math.floor((pricePerPaxMinor * (request.depositPercentBasisPoints ?? 0)) / FULL)
      : request.depositType === 'Fixed'
        ? Math.min(request.depositAmountMinor ?? 0, pricePerPaxMinor)
        : 0;
  const balance = pricePerPaxMinor - deposit;
  const lines: ScheduleLine[] = [];

  if (deposit > 0)
    lines.push({ label: 'Deposit', dueDate: bookingDate, amountMinor: deposit, dueNow: true });
  if (balance <= 0) return lines;

  if (request.installments.length === 0) {
    const cutoff = plusDays(request.departureDate, -request.cutoffDaysBefore);
    const dueNow = cutoff <= bookingDate;
    lines.push({
      label: deposit > 0 ? 'Balance' : 'Full price',
      dueDate: dueNow ? bookingDate : cutoff,
      amountMinor: balance,
      dueNow,
    });
    return lines;
  }

  let allocated = 0;
  request.installments.forEach((item, index) => {
    const last = index === request.installments.length - 1;
    const amount = last
      ? balance - allocated
      : Math.floor((balance * item.percentOfBalanceBasisPoints) / FULL);
    allocated += amount;

    const due =
      item.dueBasis === 'FromBooking'
        ? plusDays(bookingDate, item.dueOffsetDays)
        : plusDays(request.departureDate, -item.dueOffsetDays);
    const dueNow = due <= bookingDate;

    lines.push({
      label: `Payment ${index + 1}`,
      dueDate: dueNow ? bookingDate : due,
      amountMinor: amount,
      dueNow,
    });
  });

  return lines;
}
