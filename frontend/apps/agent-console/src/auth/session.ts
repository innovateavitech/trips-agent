import type { SessionUser } from './auth-api';

/**
 * The console's session, as a small state machine.
 *
 *            restored(user)                     signed-out / expired
 *   restoring ─────────────▶ signed-in ◀──────▶ signed-out
 *       │                      ▲   │  signed-in
 *       └── restored(null) ────┼───┘
 *                              └── agency-switched (stays signed-in)
 *
 * It is a reducer rather than a handful of `useState`s so every transition is a
 * pure function that can be tested without rendering anything — and so the two
 * awkward races below are handled in one visible place.
 */

export type SessionState =
  | { status: 'restoring' }
  | { status: 'signed-in'; user: SessionUser }
  | { status: 'signed-out'; reason: 'expired' | 'unavailable' | null };

export type SessionEvent =
  /** The page-load check finished: a user, or nobody. */
  | { type: 'restored'; user: SessionUser | null }
  /** The page-load check could not reach the API at all. */
  | { type: 'restore-failed' }
  | { type: 'signed-in'; user: SessionUser }
  | { type: 'agency-switched'; user: SessionUser }
  /** The agent chose to sign out. */
  | { type: 'signed-out' }
  /** The API refused the refresh token: expired, revoked or reused. */
  | { type: 'expired' };

export const initialSessionState: SessionState = { status: 'restoring' };

export function sessionReducer(state: SessionState, event: SessionEvent): SessionState {
  switch (event.type) {
    case 'restored':
      // Race: the agent signed in on the sign-in page while the page-load check
      // was still out. That check's answer is older than the sign-in, and must
      // not sign them straight back out.
      if (state.status !== 'restoring') return state;
      return event.user
        ? { status: 'signed-in', user: event.user }
        : { status: 'signed-out', reason: null };

    case 'restore-failed':
      if (state.status !== 'restoring') return state;
      return { status: 'signed-out', reason: 'unavailable' };

    case 'signed-in':
      return { status: 'signed-in', user: event.user };

    case 'agency-switched':
      // Only meaningful for someone signed in. A switch finishing after a
      // sign-out must not resurrect the session.
      return state.status === 'signed-in' ? { status: 'signed-in', user: event.user } : state;

    case 'signed-out':
      return { status: 'signed-out', reason: null };

    case 'expired':
      // Race: a request that was in flight when the agent signed out comes back
      // 401. They are already out; telling them their session "expired" would
      // be wrong and alarming.
      return state.status === 'signed-in' ? { status: 'signed-out', reason: 'expired' } : state;
  }
}
