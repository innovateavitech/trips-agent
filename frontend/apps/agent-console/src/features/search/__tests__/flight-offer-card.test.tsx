// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { formatMoneyShort } from '@trips/utils';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { FlightOfferCard } from '../components/flight-offer-card';
import type { FlightOffer, OfferPrice } from '../types';

afterEach(cleanup);

const NET = 11_500_000;
const MARKUP = 920_000;
const SELL = NET + MARKUP;

function offer(margin: OfferPrice['margin']): FlightOffer {
  return {
    id: 'fl_1',
    journeys: [
      {
        segments: [
          {
            carrierCode: 'QI',
            carrierName: 'Ibom Air',
            flightNumber: 'QI 0321',
            origin: 'LOS',
            destination: 'ABV',
            departsAt: '2026-09-18T07:30',
            arrivesAt: '2026-09-18T08:45',
            durationMinutes: 75,
          },
        ],
        durationMinutes: 75,
        stops: 0,
      },
    ],
    cabin: 'economy',
    seatsLeft: 2,
    terms: {
      fareFamily: 'Saver',
      refundable: false,
      cancellation: 'Non-refundable. Unused airport taxes can be claimed back.',
      changes: 'Date changes for ₦15,000 plus any fare difference.',
      checkedBaggage: '15 kg',
      cabinBaggage: '7 kg',
    },
    price: { currency: 'NGN', sellMinor: SELL, margin },
  };
}

function renderCard(margin: OfferPrice['margin'], { expired = false } = {}) {
  const onSelect = vi.fn();
  render(
    <FlightOfferCard
      offer={offer(margin)}
      priceCaption="Total for 1 adult"
      expired={expired}
      onSelect={onSelect}
    />,
  );
  return { onSelect };
}

describe('FlightOfferCard', () => {
  it('shows the sell price to everyone', () => {
    renderCard(null);

    expect(screen.getByText(formatMoneyShort(SELL, 'NGN'))).toBeTruthy();
  });

  it('shows neither the net rate nor the margin to someone without margin.view', () => {
    renderCard(null);

    expect(screen.queryByText('Net')).toBeNull();
    expect(screen.queryByText('Your margin')).toBeNull();
    expect(screen.queryByText(formatMoneyShort(NET, 'NGN'))).toBeNull();
    expect(screen.queryByText(formatMoneyShort(MARKUP, 'NGN'))).toBeNull();
  });

  it('shows the net rate and the margin to someone who holds margin.view', () => {
    renderCard({ netMinor: NET, markupMinor: MARKUP });

    expect(screen.getByText(formatMoneyShort(NET, 'NGN'))).toBeTruthy();
    expect(screen.getByText(formatMoneyShort(MARKUP, 'NGN'))).toBeTruthy();
  });

  it('puts the refund terms on the face of the card and the full rules one click away, before selection', () => {
    const { onSelect } = renderCard(null);

    expect(screen.getByText('Non-refundable')).toBeTruthy();
    const toggle = screen.getByRole('button', { name: /fare rules/i });
    expect(toggle.getAttribute('aria-expanded')).toBe('false');

    fireEvent.click(toggle);

    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(
      screen.getByText(/Unused airport taxes can be claimed back/).closest('[hidden]'),
    ).toBeNull();
    expect(onSelect).not.toHaveBeenCalled();
  });

  it('warns when the fare is nearly gone', () => {
    renderCard(null);

    expect(screen.getByText('2 seats left at this fare')).toBeTruthy();
  });

  it('hands the fare over when it is selected', () => {
    const { onSelect } = renderCard(null);

    fireEvent.click(screen.getByRole('button', { name: 'Select' }));

    expect(onSelect).toHaveBeenCalledWith(expect.objectContaining({ id: 'fl_1' }));
  });

  it('cannot be selected once the fares have expired, because the price is no longer real', () => {
    const { onSelect } = renderCard(null, { expired: true });

    const select = screen.getByRole('button', { name: 'Select' });
    fireEvent.click(select);

    expect(select.hasAttribute('disabled')).toBe(true);
    expect(onSelect).not.toHaveBeenCalled();
  });
});
