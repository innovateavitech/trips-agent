import { defineConfig, loadEnv } from 'vite';
import react from '@vitejs/plugin-react';

/**
 * In development the console calls its own origin, and this proxy forwards
 * `/api` to the API. The browser never makes a cross-origin request, so the API
 * needs no CORS policy for the console, and the paths the generated client uses
 * (`/api/v1/…`) work unchanged in production behind one host.
 *
 * `VITE_API_PROXY_TARGET` defaults to the API's `http` launch profile.
 */
export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, '.', 'VITE_');
  const apiTarget = env['VITE_API_PROXY_TARGET'] || 'http://localhost:5002';

  return {
    plugins: [react()],
    server: {
      port: 5173,
      proxy: { '/api': { target: apiTarget, changeOrigin: true } },
    },
  };
});
