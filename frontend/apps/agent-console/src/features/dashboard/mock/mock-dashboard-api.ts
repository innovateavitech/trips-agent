import type { DashboardApi } from '../dashboard-api';
import type {
  BookingSummary,
  HomeOverview,
  InsightItem,
  TransactionRow,
  UpcomingItem,
} from '../types';

/**
 * ============================================================================
 *  TEMPORARY. Delete this folder when the orders endpoints (#41, #42) land.
 * ============================================================================
 *
 * Realistic Nigerian bookings so the dashboard can be seen doing its job.
 * Times are relative to "now", so the ticket-time-limit countdowns are always
 * live rather than frozen in the past. Answers after ~300ms so the loading
 * state is visible in development.
 */

const HOUR = 60 * 60 * 1000;
const LATENCY_MS = 300;

function at(offsetMs: number): string {
  return new Date(Date.now() + offsetMs).toISOString();
}

function bookings(): BookingSummary[] {
  return [
    {
      reference: 'TRP-8K2P9A',
      leadTraveller: 'Emeka Nwosu',
      travellerCount: 1,
      product: 'flight',
      origin: 'PHC',
      destination: 'LOS',
      carrier: 'Ibom Air QI 0321',
      departsAt: at(26 * HOUR),
      status: 'awaiting_ticket',
      sellMinor: 11_890_000,
      currency: 'NGN',
      ticketTimeLimit: at(2 * HOUR + 40 * 60 * 1000),
    },
    {
      reference: 'TRP-8K2L1E',
      leadTraveller: 'Ngozi Eze',
      travellerCount: 2,
      product: 'flight',
      origin: 'QOW',
      destination: 'LOS',
      carrier: 'Arik Air W3 0112',
      departsAt: at(3 * 24 * HOUR),
      status: 'failed',
      sellMinor: 20_950_000,
      currency: 'NGN',
      ticketTimeLimit: null,
    },
    {
      reference: 'TRP-8K2R6H',
      leadTraveller: 'Folake Adeyemi',
      travellerCount: 4,
      product: 'flight',
      origin: 'LOS',
      destination: 'KAN',
      carrier: 'United Nigeria UN 0213',
      departsAt: at(5 * 24 * HOUR),
      status: 'awaiting_ticket',
      sellMinor: 38_200_000,
      currency: 'NGN',
      ticketTimeLimit: at(20 * HOUR),
    },
    {
      reference: 'TRP-8K2Q4F',
      leadTraveller: 'Adaeze Okafor',
      travellerCount: 1,
      product: 'flight',
      origin: 'LOS',
      destination: 'ABV',
      carrier: 'Air Peace P4 7121',
      departsAt: at(18 * HOUR),
      status: 'ticketed',
      sellMinor: 14_250_000,
      currency: 'NGN',
      ticketTimeLimit: null,
    },
    {
      reference: 'TRP-8K2M7D',
      leadTraveller: 'Tunde Bakare',
      travellerCount: 3,
      product: 'bus',
      origin: 'Lagos',
      destination: 'Abuja',
      carrier: 'GIG Mobility',
      departsAt: at(40 * HOUR),
      status: 'ticketed',
      sellMinor: 8_700_000,
      currency: 'NGN',
      ticketTimeLimit: null,
    },
    {
      reference: 'TRP-8K2N3C',
      leadTraveller: 'Hauwa Bello',
      travellerCount: 1,
      product: 'flight',
      origin: 'KAN',
      destination: 'ABV',
      carrier: 'United Nigeria UN 0503',
      departsAt: at(2 * 24 * HOUR),
      status: 'ticketed',
      sellMinor: 9_640_000,
      currency: 'NGN',
      ticketTimeLimit: null,
    },
    {
      reference: 'TRP-8K2K5G',
      leadTraveller: 'Ibrahim Musa',
      travellerCount: 2,
      product: 'bus',
      origin: 'Abuja',
      destination: 'Kano',
      carrier: 'ABC Transport',
      departsAt: at(4 * 24 * HOUR),
      status: 'confirmed',
      sellMinor: 4_300_000,
      currency: 'NGN',
      ticketTimeLimit: null,
    },
  ];
}

/**
 * The Home page's "For you today" folds in booking urgency (issue-ticket
 * deadlines, failed confirmations) alongside collection/insight flavor —
 * there is no separate collections feature yet, so those two rows are static.
 */
function forYouToday(attention: BookingSummary[], overdueInvoice: TransactionRow): InsightItem[] {
  const fromBookings: InsightItem[] = attention.slice(0, 2).map((booking) => {
    if (booking.status === 'failed') {
      return {
        id: booking.reference,
        tone: 'destructive',
        message: `${booking.carrier} did not confirm ${booking.leadTraveller}'s booking — it needs your decision.`,
        actions: [{ label: 'Resolve', href: '/resolution' }],
      };
    }
    return {
      id: booking.reference,
      tone: 'warning',
      message: `${booking.leadTraveller}'s ticket with ${booking.carrier} must be issued before the airline's time limit.`,
      actions: [{ label: 'Issue ticket', href: `/bookings/${booking.reference}` }],
    };
  });

  return [
    ...fromBookings,
    {
      id: 'insight-overdue-invoice',
      tone: 'destructive',
      message: `${overdueInvoice.name}'s invoice is 61 days overdue — no response to the last two reminders.`,
      actions: [
        { label: 'View', href: overdueInvoice.href },
        { label: 'Send reminder', href: overdueInvoice.href },
      ],
    },
    {
      id: 'insight-collection-rate',
      tone: 'info',
      message: 'Collection rate improved to 82% — up 8.4% from last month.',
      actions: [{ label: 'View report', href: '/analytics' }],
    },
  ];
}

function upcomingTravel(all: BookingSummary[]): UpcomingItem[] {
  return all
    .filter(
      (b) => b.status === 'ticketed' || b.status === 'awaiting_ticket' || b.status === 'confirmed',
    )
    .slice(0, 4)
    .map((booking) => ({
      id: booking.reference,
      kind: 'travel',
      product: booking.product,
      title: `${firstName(booking.leadTraveller)}'s trip to ${booking.destination}`,
      meta: [
        `${booking.origin} - ${booking.destination}`,
        formatShortDate(booking.departsAt),
        booking.carrier,
      ],
      href: `/bookings/${booking.reference}`,
    }));
}

/** No group-departure booking data exists yet — this tab is illustrative until it does. */
function upcomingCatalogue(): UpcomingItem[] {
  return [
    {
      id: 'catalogue-zanzibar',
      kind: 'catalogue',
      product: 'tour',
      title: 'Zanzibar Getaway — 5 nights',
      meta: ['Departs Jun 12', '8 of 12 seats taken', 'Deposit due May 20'],
      href: '/catalog',
    },
    {
      id: 'catalogue-dubai',
      kind: 'catalogue',
      product: 'tour',
      title: 'Dubai Shopping Festival',
      meta: ['Departs Jul 3', '5 of 15 seats taken', 'Deposit due Jun 10'],
      href: '/catalog',
    },
  ];
}

function firstName(fullName: string): string {
  return fullName.split(' ')[0] ?? fullName;
}

function invoiceRows(all: BookingSummary[]): TransactionRow[] {
  const statuses: TransactionRow['status'][] = ['overdue', 'pending', 'draft', 'overdue'];
  return all.slice(0, 4).map((booking, index) => ({
    id: booking.reference,
    name: firstName(booking.leadTraveller),
    amountMinor: booking.sellMinor,
    currency: booking.currency,
    reference: `Invoice No. ${7720 + index}`,
    status: statuses[index] ?? 'pending',
    href: `/invoices/${booking.reference}`,
  }));
}

function paymentRows(all: BookingSummary[]): TransactionRow[] {
  return all
    .filter((b) => b.status === 'ticketed' || b.status === 'confirmed')
    .slice(0, 4)
    .map((booking, index) => ({
      id: `payment-${booking.reference}`,
      name: firstName(booking.leadTraveller),
      amountMinor: booking.sellMinor,
      currency: booking.currency,
      reference: `Payment ref. PMT-${3300 + index}`,
      status: 'paid' as const,
      href: `/invoices/${booking.reference}`,
    }));
}

const FALLBACK_INVOICE: TransactionRow = {
  id: 'fallback',
  name: 'Your customer',
  amountMinor: 0,
  currency: 'NGN',
  reference: 'Invoice No. 0000',
  status: 'overdue',
  href: '/invoices',
};

const shortDateFormat = new Intl.DateTimeFormat('en-NG', {
  month: 'short',
  day: 'numeric',
  hour: '2-digit',
  minute: '2-digit',
  hourCycle: 'h23',
  timeZone: 'Africa/Lagos',
});

function formatShortDate(iso: string): string {
  return shortDateFormat.format(new Date(iso));
}

function buildHomeOverview(all: BookingSummary[], attention: BookingSummary[]): HomeOverview {
  const invoices = invoiceRows(all);

  return {
    earnings: {
      totalMinor: 1_045_035_602,
      currency: 'NGN',
      changeBasisPoints: 3000,
      composition: [
        { label: 'Flights & buses', valueMinor: 627_000_000 },
        { label: 'Tours & packages', valueMinor: 313_000_000 },
      ],
    },
    forYouToday: forYouToday(attention, invoices[0] ?? FALLBACK_INVOICE),
    upcomingItems: [...upcomingTravel(all), ...upcomingCatalogue()],
    invoices,
    payments: paymentRows(all),
  };
}

export const mockDashboardApi: DashboardApi = {
  async getOverview() {
    await new Promise((resolve) => setTimeout(resolve, LATENCY_MS));
    const all = bookings();

    const needsAttention = all
      .filter((b) => b.status === 'awaiting_ticket' || b.status === 'failed')
      // A deadline beats no deadline; the nearest deadline beats a later one.
      .sort((a, b) => {
        if (a.ticketTimeLimit && b.ticketTimeLimit) {
          return a.ticketTimeLimit.localeCompare(b.ticketTimeLimit);
        }
        return a.ticketTimeLimit ? -1 : b.ticketTimeLimit ? 1 : 0;
      });

    return {
      needsAttention,
      recentBookings: all.slice(0, 6),
      home: buildHomeOverview(all, needsAttention),
    };
  },
};
