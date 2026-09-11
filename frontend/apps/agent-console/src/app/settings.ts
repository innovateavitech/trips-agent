/**
 * Build-time switches, read from `VITE_*` variables in the repository's `.env`.
 *
 * Kept in one file so a reader can see every switch the console has, and so a
 * test can tell which mode it is in without reaching for `import.meta.env`.
 */

/**
 * `api`  — the default. Sign-in, refresh and the session all go to the real
 *          `/api/v1/auth` endpoints, so the API has to be running.
 *
 * `mock` — a stand-in session that never touches the network: any email signs
 *          in, and the agency switcher offers a principal with two sub-agents.
 *          For demos and for building screens with no backend running. It is
 *          chosen explicitly, never fallen back to, so nobody mistakes a
 *          mocked sign-in for a working one.
 *
 * Choose `mock` with `pnpm dev:demo` (Vite's `--mode demo`), or by setting
 * `VITE_AUTH_MODE=mock` in the shell or in `apps/agent-console/.env.local`.
 */
export type AuthMode = 'api' | 'mock';

const configured: unknown = import.meta.env['VITE_AUTH_MODE'];

export const AUTH_MODE: AuthMode =
  configured === 'mock' || import.meta.env.MODE === 'demo' ? 'mock' : 'api';
