// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError } from '../../../api/errors';
import { FlightResults } from '../components/flight-results';
import type { SearchView } from '../search-api';
import type { FlightOffer, SearchResult } from '../types';

afterEach(cleanup);

const PASSENGERS = { adults: 1, children: 0, infants: 0 };

function view(
  overrides: Partial<SearchView<SearchResult<FlightOffer>>>,
): SearchView<SearchResult<FlightOffer>> {
  return {
    isPending: false,
    isError: false,
    isFetching: false,
    data: undefined,
    error: null,
    refetch: vi.fn(),
    ...overrides,
  };
}

function renderResults(query: SearchView<SearchResult<FlightOffer>>) {
  const onResearch = vi.fn();
  render(
    <FlightResults
      query={query}
      passengers={PASSENGERS}
      onResearch={onResearch}
      onSelect={vi.fn()}
    />,
  );
  return { onResearch };
}

describe('FlightResults', () => {
  it('shows the shape of the results while the airlines answer, and says how long it may take', () => {
    renderResults(view({ isPending: true }));

    expect(screen.getByRole('status').textContent).toMatch(/up to 20 seconds/);
  });

  it('offers to try again when the search fails — never an empty list', () => {
    const refetch = vi.fn();
    renderResults(
      view({
        isError: true,
        error: new ApiError(504, 'The airline systems did not answer in time.'),
        refetch,
      }),
    );

    const alert = screen.getByRole('alert');
    expect(alert.textContent).toMatch(/did not answer in time/);
    expect(alert.textContent).toMatch(/Nothing has been booked or charged/);
    expect(screen.queryByText(/No flights/)).toBeNull();
    expect(screen.queryByRole('list')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));

    expect(refetch).toHaveBeenCalledTimes(1);
  });

  it('says plainly when nothing flies, which is a different answer from a failure', () => {
    const { onResearch } = renderResults(
      view({
        data: { offers: [], searchedAt: '2026-09-11T10:00:00Z', expiresAt: '2026-09-11T10:10:00Z' },
      }),
    );

    expect(screen.getByText('No flights on that route and date')).toBeTruthy();
    expect(screen.queryByRole('alert')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Search again' }));

    expect(onResearch).toHaveBeenCalledTimes(1);
  });
});
