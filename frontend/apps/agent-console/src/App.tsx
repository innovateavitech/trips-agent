import { useState } from 'react';
import { QueryClientProvider } from '@tanstack/react-query';
import { RouterProvider } from 'react-router-dom';
import { createQueryClient } from './api/query-client';
import { AuthProvider } from './auth/auth-context';
import { WalletApiProvider } from './features/wallet';
import { mockWalletApi } from './features/wallet/mock/mock-wallet-api';
import { router } from './routes';

/**
 * Provider order matters.
 *
 * `QueryClientProvider` sits OUTSIDE `AuthProvider` because the agency switcher
 * calls `useQueryClient()` to drop the previous agency's cached data. React
 * Router sits inside both, so every route can reach the session and the cache.
 */
export function App() {
  /* Created once per mount rather than at module scope, so each test gets a
     clean cache instead of inheriting the previous test's. */
  const [queryClient] = useState(createQueryClient);

  return (
    <QueryClientProvider client={queryClient}>
      <AuthProvider>
        {/*
          TEMPORARY, and the line the wallet's mock adapter was written to be
          swapped at: when #26 ships the real top-up endpoints, this becomes the
          real client and `features/wallet/mock/` is deleted. Until then the
          wallet screens that landed in #86 run on in-memory data.

          They were previously unreachable — nothing routed to them and no
          provider was mounted, so `useWalletApi()` would have thrown. Wiring
          this up is what makes #86's work actually visible in the app.
        */}
        <WalletApiProvider value={mockWalletApi}>
          <RouterProvider router={router} />
        </WalletApiProvider>
      </AuthProvider>
    </QueryClientProvider>
  );
}
