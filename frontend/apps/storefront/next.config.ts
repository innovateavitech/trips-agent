import type { NextConfig } from 'next';
import { fixedSecurityHeaders } from './lib/security-headers';

const config: NextConfig = {
  // Shared workspace packages are TypeScript source, so Next must transpile them.
  transpilePackages: ['@trips/api-client', '@trips/ui', '@trips/utils'],

  // No "X-Powered-By: Next.js". A traveller's response should not say what built the site, and a
  // scanner should not be told which exploits to try (issue 107).
  poweredByHeader: false,

  // The fixed security headers on every response, static assets included. The Content-Security-Policy
  // needs a nonce per request, so middleware.ts sets that one. See lib/security-headers.ts.
  async headers() {
    return [{ source: '/:path*', headers: fixedSecurityHeaders }];
  },
};

export default config;
