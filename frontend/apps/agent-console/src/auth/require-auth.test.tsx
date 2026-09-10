import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import type { Location } from 'react-router-dom';
import { RequireAuth } from './require-auth';
import { useAuth } from './auth-context';
import type { AuthStatus } from './auth-context';

/**
 * Covers acceptance criterion 2 on issue #48 — "auth guard redirecting to
 * login, preserving the intended destination".
 *
 * The auth module is mocked rather than mounted: a real `AuthProvider` runs the
 * silent bootstrap and starts hitting the network, and what is under test here
 * is the guard's DECISION, not how the session was obtained.
 *
 * `MemoryRouter` rather than `createMemoryRouter`: the data router builds a
 * `Request` on every navigation, and jsdom's `AbortSignal` is not the instance
 * Node's `Request` accepts, so the redirect throws inside the router. The guard
 * uses only `Navigate`/`Outlet`/`useLocation`, none of which need a data router.
 */
vi.mock('./auth-context', () => ({
  useAuth: vi.fn(),
}));

const useAuthMock = vi.mocked(useAuth);

function setStatus(status: AuthStatus) {
  useAuthMock.mockReturnValue({
    status,
    session: null,
    signIn: vi.fn(),
    signOut: vi.fn().mockResolvedValue(undefined),
    switchAgency: vi.fn(),
  });
}

/** Renders the redirect state so a test can assert on what was preserved. */
function LoginProbe() {
  const location = useLocation();
  const state = location.state as { from?: Location } | null;
  const from = state?.from;
  return (
    <div>
      <span>Login screen</span>
      <span data-testid="from">
        {from ? `${from.pathname}${from.search ?? ''}${from.hash ?? ''}` : 'none'}
      </span>
    </div>
  );
}

function renderGuardedAt(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route element={<RequireAuth />}>
          <Route path="/bookings/:id" element={<div>Booking detail</div>} />
        </Route>
        <Route path="/login" element={<LoginProbe />} />
      </Routes>
    </MemoryRouter>,
  );
}

beforeEach(() => {
  useAuthMock.mockReset();
});

describe('RequireAuth', () => {
  it('renders the route when authenticated', () => {
    setStatus('authenticated');
    renderGuardedAt('/bookings/abc');

    expect(screen.getByText('Booking detail')).toBeTruthy();
  });

  it('does NOT redirect while the session is still bootstrapping', () => {
    setStatus('bootstrapping');
    renderGuardedAt('/bookings/abc');

    // The regression this guards: a signed-in agent pressing F5 must not be
    // bounced to /login while the refresh cookie is still being checked.
    expect(screen.queryByText('Login screen')).toBeNull();
    expect(screen.queryByText('Booking detail')).toBeNull();
    expect(screen.getByRole('status')).toBeTruthy();
  });

  it('redirects an anonymous visitor to login, preserving the full destination', () => {
    setStatus('anonymous');
    renderGuardedAt('/bookings/abc?tab=passengers#seat');

    expect(screen.getByText('Login screen')).toBeTruthy();
    // Path, query AND hash — an agent following a link to a specific passenger
    // tab should land back on that tab, not a generic dashboard.
    expect(screen.getByTestId('from').textContent).toBe('/bookings/abc?tab=passengers#seat');
  });
});
