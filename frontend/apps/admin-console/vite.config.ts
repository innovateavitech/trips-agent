import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

/**
 * Where TripsAgent.Api listens — see services/TripsAgent.Api/Properties/launchSettings.json.
 * 5002 rather than the usual 5000, because macOS AirPlay Receiver holds 5000.
 */
const API_ORIGIN = 'http://localhost:5002';

/**
 * The console calls relative `/api/...` URLs and the dev server forwards them to the API.
 *
 * This is not optional. The API has no CORS configured (that is issue 107), so a page on
 * localhost:5174 is not allowed to read responses from localhost:5002. Through the proxy the
 * browser only ever talks to its own origin, and the question never comes up.
 *
 * It also makes the KYB document links work: the API hands back signed paths such as
 * `/api/v1/admin/kyb/documents/{id}?expires=…&signature=…`, which open in a new tab on this
 * origin and are forwarded like any other call.
 */
const apiProxy = {
  '/api': { target: API_ORIGIN, changeOrigin: false },
};

export default defineConfig({
  plugins: [react()],
  server: { port: 5174, strictPort: true, proxy: apiProxy },
  preview: { port: 5174, strictPort: true, proxy: apiProxy },
});
