import type { NextConfig } from 'next';

const config: NextConfig = {
  // Shared workspace packages are TypeScript source, so Next must transpile them.
  transpilePackages: ['@trips/ui', '@trips/utils'],
};

export default config;
