/**
 * Base URL of the .NET API. Set `VITE_API_BASE_URL` in `.env.local`.
 *
 * Defaults to the Kestrel dev port so `pnpm dev` works with no config, which
 * matters for a new developer on their first afternoon.
 */
export const API_BASE_URL: string = import.meta.env['VITE_API_BASE_URL'] ?? 'http://localhost:5000';

/** Prefixes a path with the API base. `apiUrl('/auth/refresh')`. */
export function apiUrl(path: string): string {
  return `${API_BASE_URL.replace(/\/$/, '')}${path.startsWith('/') ? path : `/${path}`}`;
}
