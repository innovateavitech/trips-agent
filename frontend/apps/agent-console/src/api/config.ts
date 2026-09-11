/**
 * Where the API lives, from `VITE_API_BASE_URL`.
 *
 * Empty by default, which means "this page's own origin". In development the
 * Vite dev server proxies `/api` to the API (see `vite.config.ts`), so the
 * browser never makes a cross-origin request and the API needs no CORS policy.
 * In a deployed environment the console and the API sit behind one host.
 *
 * The paths in the generated client already start with `/api/v1/…`, so this is
 * an origin only — never a path.
 */
const configured: unknown = import.meta.env['VITE_API_BASE_URL'];

export const API_BASE_URL: string =
  typeof configured === 'string' ? configured.replace(/\/+$/, '') : '';
