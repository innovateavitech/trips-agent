import { ApiError } from '../../api/errors';
import type { AgencyOption, AuthApi, SessionUser } from '../auth-api';

/**
 * ============================================================================
 *  A STAND-IN SESSION. Used only when `VITE_AUTH_MODE=mock`.
 * ============================================================================
 *
 * For demos and for building screens without the API running. It never touches
 * the network and never sees a real token: any email signs in, except with the
 * password `wrong`, which shows the failed sign-in state.
 *
 * It also stands in for something the API cannot do yet — switching between a
 * principal agency and its sub-agents (M3) — so the agency switcher can be seen
 * working. The real adapter offers only the agency in the token.
 *
 * The session is kept in `sessionStorage` so it survives the full-page reload
 * the wallet's stand-in payment page does, exactly like the real one would.
 */

const STORE_KEY = 'trips.mock.session';
const LATENCY_MS = 350;

export const MOCK_AGENCIES: AgencyOption[] = [
  { id: 'agc_lekki_horizon', name: 'Lekki Horizon Travels', kind: 'principal' },
  { id: 'agc_horizon_abuja', name: 'Horizon Travels Abuja', kind: 'sub_agent' },
  { id: 'agc_horizon_phc', name: 'Horizon Travels Port Harcourt', kind: 'sub_agent' },
];

interface StoredSession {
  email: string;
  agencyId: string;
}

const delay = () => new Promise((resolve) => setTimeout(resolve, LATENCY_MS));

function read(): StoredSession | null {
  try {
    const raw = sessionStorage.getItem(STORE_KEY);
    return raw ? (JSON.parse(raw) as StoredSession) : null;
  } catch {
    return null;
  }
}

function write(session: StoredSession | null): void {
  try {
    if (session) sessionStorage.setItem(STORE_KEY, JSON.stringify(session));
    else sessionStorage.removeItem(STORE_KEY);
  } catch {
    // Storage blocked: the mock session lasts until the next reload.
  }
}

function toUser(session: StoredSession): SessionUser {
  const agency = MOCK_AGENCIES.find((a) => a.id === session.agencyId) ?? MOCK_AGENCIES[0]!;
  return {
    userId: 'usr_mock_0001',
    email: session.email,
    roles: [agency.kind === 'principal' ? 'Owner' : 'Agent'],
    agency,
  };
}

export const mockAuthApi: AuthApi = {
  async signIn({ email, password }) {
    await delay();
    if (password === 'wrong') {
      // The API's own wording, so the demo shows what production says.
      throw new ApiError(401, 'That email address and password do not match.');
    }
    const session = { email: email.trim().toLowerCase(), agencyId: MOCK_AGENCIES[0]!.id };
    write(session);
    return toUser(session);
  },

  async restore() {
    await delay();
    const session = read();
    return session ? toUser(session) : null;
  },

  async signOut() {
    write(null);
  },

  async listAgencies() {
    await delay();
    return MOCK_AGENCIES;
  },

  async switchAgency(agencyId) {
    await delay();
    const session = read();
    if (!session)
      throw new ApiError(401, 'That session has expired.', 'Sign in again to continue.');
    if (!MOCK_AGENCIES.some((a) => a.id === agencyId)) {
      throw new ApiError(403, 'You cannot act for that agency.');
    }
    const next = { ...session, agencyId };
    write(next);
    return toUser(next);
  },
};
