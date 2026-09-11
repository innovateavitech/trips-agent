import type { DashboardApi } from '../dashboard-api';
import type { BookingSummary } from '../types';

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

    return { needsAttention, recentBookings: all.slice(0, 6) };
  },
};
