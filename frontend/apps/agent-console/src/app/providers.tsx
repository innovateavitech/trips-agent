import { QueryClientProvider, type QueryClient } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import type { AuthApi } from '../auth/auth-api';
import { AuthProvider } from '../auth/auth-provider';
import { DashboardApiProvider, type DashboardApi } from '../features/dashboard';
import { WalletApiProvider, type WalletApi } from '../features/wallet';

export interface AppAdapters {
  auth: AuthApi;
  wallet: WalletApi;
  dashboard: DashboardApi;
}

/**
 * Everything the screens need from outside React, in one place. `main.tsx`
 * chooses the adapters; tests pass their own. Swapping a mock for a real
 * adapter is a change to `main.tsx` and nothing else.
 */
export function AppProviders({
  queryClient,
  adapters,
  children,
}: {
  queryClient: QueryClient;
  adapters: AppAdapters;
  children: ReactNode;
}) {
  return (
    <QueryClientProvider client={queryClient}>
      <AuthProvider api={adapters.auth}>
        <WalletApiProvider value={adapters.wallet}>
          <DashboardApiProvider value={adapters.dashboard}>{children}</DashboardApiProvider>
        </WalletApiProvider>
      </AuthProvider>
    </QueryClientProvider>
  );
}
