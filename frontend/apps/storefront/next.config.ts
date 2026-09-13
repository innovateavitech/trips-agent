import type { NextConfig } from 'next';

const config: NextConfig = {
  // Shared workspace packages are TypeScript source, so Next must transpile them.
  transpilePackages: ['@trips/api-client', '@trips/ui', '@trips/utils'],
};

export default config;
