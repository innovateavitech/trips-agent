import { QueryClientProvider, type QueryClient } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import type { AuthApi } from '../auth/auth-api';
import { AuthProvider } from '../auth/auth-provider';
import { CatalogApiProvider, type CatalogApi } from '../features/catalog';
import { DashboardApiProvider, type DashboardApi } from '../features/dashboard';
import { DeparturesApiProvider, type DeparturesApi } from '../features/departures';
import { SearchApiProvider, type SearchApi } from '../features/search';
import { BookingFlowApiProvider, type BookingFlowApi } from '../features/booking';
import { BookingsApiProvider, type BookingsApi } from '../features/bookings';
import { WalletApiProvider, type WalletApi } from '../features/wallet';

export interface AppAdapters {
  auth: AuthApi;
  wallet: WalletApi;
  dashboard: DashboardApi;
  search: SearchApi;
  bookingFlow: BookingFlowApi;
  bookings: BookingsApi;
  catalog: CatalogApi;
  departures: DeparturesApi;
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
          <DashboardApiProvider value={adapters.dashboard}>
            <SearchApiProvider value={adapters.search}>
              <BookingFlowApiProvider value={adapters.bookingFlow}>
                <BookingsApiProvider value={adapters.bookings}>
                  <CatalogApiProvider value={adapters.catalog}>
                    <DeparturesApiProvider value={adapters.departures}>
                      {children}
                    </DeparturesApiProvider>
                  </CatalogApiProvider>
                </BookingsApiProvider>
              </BookingFlowApiProvider>
            </SearchApiProvider>
          </DashboardApiProvider>
        </WalletApiProvider>
      </AuthProvider>
    </QueryClientProvider>
  );
}
