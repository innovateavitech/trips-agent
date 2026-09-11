// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { formatMoneyShort } from '@trips/utils';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { BookingProgressCard } from '../components/booking-progress';
import { PriceChangeDialog } from '../components/price-change-dialog';

afterEach(cleanup);

describe('PriceChangeDialog', () => {
  const rise = {
    sellMinor: 10_350_000,
    searchedSellMinor: 10_000_000,
    currency: 'NGN',
    ticketTimeLimit: '2026-10-01T10:00:00Z',
  };

  it('shows what it was, what it is now and the difference, before anything goes on', () => {
    render(<PriceChangeDialog confirmation={rise} onAccept={vi.fn()} onDecline={vi.fn()} />);

    expect(screen.getByRole('dialog')).toBeTruthy();
    expect(screen.getByText('The price went up')).toBeTruthy();
    expect(screen.getByText(formatMoneyShort(10_000_000, 'NGN'))).toBeTruthy();
    expect(screen.getByText(formatMoneyShort(10_350_000, 'NGN'))).toBeTruthy();
    expect(screen.getByText(`${formatMoneyShort(350_000, 'NGN')} more`)).toBeTruthy();
  });

  it('goes on only when the agent says the customer agrees', () => {
    const onAccept = vi.fn();
    const onDecline = vi.fn();
    render(<PriceChangeDialog confirmation={rise} onAccept={onAccept} onDecline={onDecline} />);

    fireEvent.click(screen.getByRole('button', { name: 'Customer agrees — continue' }));

    expect(onAccept).toHaveBeenCalledTimes(1);
    expect(onDecline).not.toHaveBeenCalled();
  });

  it('declines when the agent says no', () => {
    const onAccept = vi.fn();
    const onDecline = vi.fn();
    render(<PriceChangeDialog confirmation={rise} onAccept={onAccept} onDecline={onDecline} />);

    fireEvent.click(screen.getByRole('button', { name: 'Don’t accept' }));

    expect(onDecline).toHaveBeenCalled();
    expect(onAccept).not.toHaveBeenCalled();
  });

  it('shows nothing while the price has not moved', () => {
    render(<PriceChangeDialog confirmation={null} onAccept={vi.fn()} onDecline={vi.fn()} />);

    expect(screen.queryByRole('dialog')).toBeNull();
  });
});

describe('BookingProgressCard', () => {
  function renderCard(
    status: 'awaiting_ticket' | 'ticketed' | 'failed',
    pnr: string | null = null,
  ) {
    render(
      <MemoryRouter>
        <BookingProgressCard progress={{ status, pnr }} reference="TRP-ABC123" carrier="Ibom Air" />
      </MemoryRouter>,
    );
  }

  it('says the airline is still confirming, and never "booking confirmed", while the ticket is pending', () => {
    renderCard('awaiting_ticket');

    expect(screen.getByText('We’re confirming with Ibom Air')).toBeTruthy();
    expect(screen.queryByText('Booking confirmed')).toBeNull();
    expect(screen.queryByText('Ticketed')).toBeNull();
  });

  it('shows the PNR and a way to the booking once it is ticketed', () => {
    renderCard('ticketed', 'QX7K2P');

    expect(screen.getByText('Booking confirmed')).toBeTruthy();
    expect(screen.getByText('QX7K2P')).toBeTruthy();
    expect(screen.getByRole('link', { name: 'View booking' }).getAttribute('href')).toBe(
      '/bookings/TRP-ABC123',
    );
  });

  it('sends a failure to the resolution queue, loudly', () => {
    renderCard('failed');

    expect(screen.getByRole('alert').textContent).toMatch(/Ibom Air did not confirm this booking/);
    expect(
      screen.getByRole('link', { name: 'Open the resolution queue' }).getAttribute('href'),
    ).toBe('/resolution');
  });
});
