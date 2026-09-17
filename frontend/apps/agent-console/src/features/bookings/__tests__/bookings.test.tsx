// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { formatMoneyShort } from '@trips/utils';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  describeDuration,
  filterBookings,
  formatDateOfBirth,
  resolutionCopy,
  splitFareEvenly,
  statusCounts,
} from '../bookings-rules';
import { ResolutionCard } from '../components/resolution-card';
import { NO_BOOKING_FILTERS, type BookingDetail, type BookingListItem } from '../types';

afterEach(cleanup);

function item(patch: Partial<BookingListItem>): BookingListItem {
  return {
    reference: 'TRP-AAA111',
    leadTraveller: 'Adaeze Okafor',
    travellerCount: 1,
    product: 'flight',
    origin: 'LOS',
    destination: 'ABV',
    carrier: 'Air Peace P4 7121',
    departsAt: '2026-10-02T06:30:00Z',
    status: 'ticketed',
    sellMinor: 14_250_000,
    currency: 'NGN',
    ticketTimeLimit: null,
    pnr: 'QX7K2P',
    bookedAt: '2026-09-30T10:00:00Z',
    ...patch,
  };
}

const failed: BookingDetail = {
  ...item({
    reference: 'TRP-FAIL01',
    status: 'failed',
    pnr: null,
    sellMinor: 20_950_000,
    carrier: 'Arik Air W3 0112',
  }),
  paidFrom: 'wallet',
  travellers: [{ type: 'ADT', name: 'Ngozi Eze', ticketNumber: null }],
  segments: [],
  price: { sellMinor: 20_950_000, margin: null },
  timeline: [],
  failure: {
    reason: 'Arik Air did not confirm the seats.',
    atRiskMinor: 20_950_000,
    paidFrom: 'wallet',
  },
};

describe('finding a booking', () => {
  const bookings = [
    item({}),
    item({
      reference: 'TRP-BBB222',
      leadTraveller: 'Tunde Bakare',
      product: 'bus',
      status: 'ticketed',
      pnr: 'GIG7Q4',
    }),
    item({ reference: 'TRP-CCC333', leadTraveller: 'Ngozi Eze', status: 'failed', pnr: null }),
  ];

  it('filters by status and by product', () => {
    expect(
      filterBookings(bookings, { ...NO_BOOKING_FILTERS, status: 'failed' }).map((b) => b.reference),
    ).toEqual(['TRP-CCC333']);
    expect(
      filterBookings(bookings, { ...NO_BOOKING_FILTERS, product: 'bus' }).map((b) => b.reference),
    ).toEqual(['TRP-BBB222']);
  });

  it('finds by PNR, reference or traveller, whatever the case', () => {
    expect(filterBookings(bookings, { ...NO_BOOKING_FILTERS, query: 'gig7q4' })).toHaveLength(1);
    expect(filterBookings(bookings, { ...NO_BOOKING_FILTERS, query: 'ngozi' })).toHaveLength(1);
    expect(filterBookings(bookings, { ...NO_BOOKING_FILTERS, query: 'TRP-AAA' })).toHaveLength(1);
  });

  it('counts each status for the filter', () => {
    expect(statusCounts(bookings)).toMatchObject({
      all: 3,
      ticketed: 2,
      failed: 1,
      awaiting_ticket: 0,
    });
  });
});

describe('splitting a fare evenly among travellers', () => {
  it('divides evenly when it divides evenly', () => {
    expect(splitFareEvenly(90_000, 3)).toEqual([30_000, 30_000, 30_000]);
  });

  it('gives the leftover kobo to the first travellers, one each, so parts sum to the total', () => {
    expect(splitFareEvenly(1_000, 3)).toEqual([334, 333, 333]);
    expect(splitFareEvenly(1_000, 3).reduce((a, b) => a + b, 0)).toBe(1_000);
  });

  it('returns the whole amount for one traveller', () => {
    expect(splitFareEvenly(20_950_000, 1)).toEqual([20_950_000]);
  });

  it('returns an empty split for zero travellers', () => {
    expect(splitFareEvenly(1_000, 0)).toEqual([]);
  });
});

describe("a segment's duration", () => {
  it('describes an hour and minutes', () => {
    expect(describeDuration('2026-09-19T11:00', '2026-09-19T12:30')).toBe('1 hr 30 min');
  });

  it('describes a whole number of hours without a redundant "0 min"', () => {
    expect(describeDuration('2026-09-19T11:00', '2026-09-19T12:00')).toBe('1 hr');
  });

  it('describes under an hour in minutes only', () => {
    expect(describeDuration('2026-09-19T11:00', '2026-09-19T11:45')).toBe('45 min');
  });

  it('is "—" for a non-positive span rather than a negative duration', () => {
    expect(describeDuration('2026-09-19T12:00', '2026-09-19T11:00')).toBe('—');
  });
});

describe('formatting a date of birth', () => {
  it('renders as day/month/year', () => {
    expect(formatDateOfBirth('1999-03-12')).toBe('12/03/1999');
  });
});

describe('resolving a failed booking', () => {
  it('puts the amount in the words of a refund, and says where it goes', () => {
    const toWallet = resolutionCopy('refund', failed);
    const toCard = resolutionCopy('refund', {
      ...failed,
      paidFrom: 'card',
      failure: { ...failed.failure!, paidFrom: 'card' },
    });

    expect(toWallet.amountMinor).toBe(20_950_000);
    expect(toWallet.body).toMatch(/your wallet/);
    expect(toCard.body).toMatch(/customer's card/);
  });

  it('promises nothing more is charged for a retry', () => {
    const retry = resolutionCopy('retry', failed);

    expect(retry.amountMinor).toBeNull();
    expect(retry.body).toMatch(/Nothing more is charged/);
  });
});

describe('ResolutionCard', () => {
  function renderCard() {
    const onResolve = vi.fn();
    const onSubstitute = vi.fn();
    render(
      <MemoryRouter>
        <ResolutionCard
          booking={failed}
          resolving={null}
          onResolve={onResolve}
          onSubstitute={onSubstitute}
        />
      </MemoryRouter>,
    );
    return { onResolve, onSubstitute };
  }

  const refund = `Refund ${formatMoneyShort(20_950_000, 'NGN')}`;

  it('shows the money at risk before anything else is decided', () => {
    renderCard();

    expect(screen.getAllByText(formatMoneyShort(20_950_000, 'NGN')).length).toBeGreaterThan(0);
    expect(screen.getByText('Arik Air did not confirm the seats.')).toBeTruthy();
  });

  it('refunds only after the amount has been shown and confirmed', () => {
    const { onResolve } = renderCard();

    fireEvent.click(screen.getByRole('button', { name: refund }));
    const dialog = screen.getByRole('dialog');

    expect(within(dialog).getByText(`${refund}?`)).toBeTruthy();
    expect(onResolve).not.toHaveBeenCalled();

    fireEvent.click(within(dialog).getByRole('button', { name: refund }));

    expect(onResolve).toHaveBeenCalledWith('refund');
  });

  it('does nothing when the agent backs out', () => {
    const { onResolve } = renderCard();

    fireEvent.click(screen.getByRole('button', { name: refund }));
    fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Not now' }));

    expect(onResolve).not.toHaveBeenCalled();
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('finds another fare without asking, because nothing moves until it is booked', () => {
    const { onResolve, onSubstitute } = renderCard();

    fireEvent.click(screen.getByRole('button', { name: 'Find another fare' }));

    expect(onSubstitute).toHaveBeenCalledTimes(1);
    expect(onResolve).not.toHaveBeenCalled();
    expect(screen.queryByRole('dialog')).toBeNull();
  });
});
