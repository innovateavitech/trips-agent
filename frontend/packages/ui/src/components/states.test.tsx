// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { EmptyState, ErrorState, LoadingState } from './states';

afterEach(cleanup);

describe('ErrorState', () => {
  it('interrupts a screen reader, because a failed load is not a pause-and-wait message', () => {
    render(<ErrorState title="We could not load your wallet" />);

    expect(screen.getByRole('alert')).toHaveProperty(
      'textContent',
      expect.stringContaining('We could not load your wallet'),
    );
  });

  it('offers "Try again" and calls back when there is something to retry', () => {
    const onRetry = vi.fn();
    render(<ErrorState title="Failed" onRetry={onRetry} />);

    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));

    expect(onRetry).toHaveBeenCalledTimes(1);
  });

  it('offers no retry button when retrying cannot help', () => {
    render(<ErrorState title="You do not have access to this agency" />);

    expect(screen.queryByRole('button')).toBeNull();
  });

  it('disables the retry button while a retry is already running, so it cannot be spammed', () => {
    render(<ErrorState title="Failed" onRetry={() => undefined} retrying />);

    expect(screen.getByRole('button', { name: 'Try again' })).toHaveProperty('disabled', true);
  });
});

describe('LoadingState', () => {
  it('announces its label politely to screen readers', () => {
    render(<LoadingState label="Loading your bookings" />);

    const status = screen.getByRole('status');
    expect(status.getAttribute('aria-live')).toBe('polite');
    expect(status.textContent).toContain('Loading your bookings');
  });
});

describe('EmptyState', () => {
  it('renders the next step it is given', () => {
    render(
      <EmptyState title="No bookings yet" action={<button type="button">Search flights</button>}>
        Bookings you make appear here.
      </EmptyState>,
    );

    expect(screen.getByText('No bookings yet')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Search flights' })).toBeTruthy();
  });

  it('leaves the title as plain text by default, so a nested state adds no outline rung', () => {
    render(<EmptyState title="No bookings yet" />);

    expect(screen.queryByRole('heading')).toBeNull();
    expect(screen.getByText('No bookings yet').tagName).toBe('P');
  });

  it('makes the title a heading at the level asked for, when it IS the page', () => {
    render(<EmptyState size="page" title="We could not find that page" headingLevel={1} />);

    const heading = screen.getByRole('heading', { level: 1 });
    expect(heading.textContent).toBe('We could not find that page');
  });

  it('keeps the title looking the same either way', () => {
    const { unmount } = render(<EmptyState title="Nothing here" />);
    const asText = screen.getByText('Nothing here').className;
    unmount();

    render(<EmptyState title="Nothing here" headingLevel={2} />);
    expect(screen.getByRole('heading', { level: 2 }).className).toBe(asText);
  });
});
