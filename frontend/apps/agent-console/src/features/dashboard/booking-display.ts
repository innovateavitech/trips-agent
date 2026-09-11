import type { BadgeProps } from '@trips/ui';
import type { BookingStatus } from './types';

/** The time zone agents work in. Deadlines and departures are shown in it. */
export const AGENCY_TIME_ZONE = 'Africa/Lagos';

export const STATUS_DISPLAY: Record<BookingStatus, { label: string; tone: BadgeProps['tone'] }> = {
  awaiting_ticket: { label: 'Awaiting ticket', tone: 'warning' },
  confirmed: { label: 'Confirmed', tone: 'info' },
  ticketed: { label: 'Ticketed', tone: 'success' },
  failed: { label: 'Needs decision', tone: 'destructive' },
  cancelled: { label: 'Cancelled', tone: 'neutral' },
};

export type Urgency = 'expired' | 'urgent' | 'soon' | 'comfortable';

/** Under this, a ticket time limit is shown as urgent. Issuing can take a while. */
export const URGENT_WITHIN_MS = 3 * 60 * 60 * 1000;
const SOON_WITHIN_MS = 24 * 60 * 60 * 1000;

/**
 * How long is left before a ticket time limit, in words an agent reads at a
 * glance: "45 min left", "2 h 40 min left", "1 d 3 h left", "Expired".
 *
 * Rounded DOWN. Telling an agent they have "3 h" when they have 2 h 59 min is
 * the direction of error that loses a booking.
 */
export function describeTimeLeft(deadline: string, now: Date): { label: string; urgency: Urgency } {
  const remaining = new Date(deadline).getTime() - now.getTime();
  if (remaining <= 0) return { label: 'Expired', urgency: 'expired' };

  const totalMinutes = Math.floor(remaining / 60_000);
  const days = Math.floor(totalMinutes / (24 * 60));
  const hours = Math.floor((totalMinutes % (24 * 60)) / 60);
  const minutes = totalMinutes % 60;

  let label: string;
  if (days > 0) label = hours > 0 ? `${days} d ${hours} h left` : `${days} d left`;
  else if (hours > 0) label = minutes > 0 ? `${hours} h ${minutes} min left` : `${hours} h left`;
  else label = totalMinutes > 0 ? `${totalMinutes} min left` : 'Under a minute left';

  const urgency: Urgency =
    remaining < URGENT_WITHIN_MS ? 'urgent' : remaining < SOON_WITHIN_MS ? 'soon' : 'comfortable';
  return { label, urgency };
}

/** "Good morning" by the clock in Lagos, not the clock on the laptop. */
export function greetingFor(now: Date): string {
  const hour = Number(
    new Intl.DateTimeFormat('en-GB', {
      hour: 'numeric',
      hourCycle: 'h23',
      timeZone: AGENCY_TIME_ZONE,
    }).format(now),
  );
  if (hour < 12) return 'Good morning';
  if (hour < 17) return 'Good afternoon';
  return 'Good evening';
}

const departureFormat = new Intl.DateTimeFormat('en-NG', {
  weekday: 'short',
  day: 'numeric',
  month: 'short',
  hour: '2-digit',
  minute: '2-digit',
  hourCycle: 'h23',
  timeZone: AGENCY_TIME_ZONE,
});

export function formatDeparture(iso: string): string {
  return departureFormat.format(new Date(iso));
}
