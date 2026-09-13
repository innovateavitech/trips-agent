import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { RouterProvider } from 'react-router-dom';
import { api, sessionTransport } from './api/client';
import { publicApi } from './api/public-client';
import { AppProviders, type AppAdapters } from './app/providers';
import { createQueryClient } from './app/query-client';
import { createAppRouter } from './app/router';
import { AUTH_MODE } from './app/settings';
import { createHttpAuthApi } from './auth/http-auth-api';
import { mockAuthApi } from './auth/mock/mock-auth-api';
import { createHttpAnalyticsApi, mockAnalyticsApi } from './features/analytics';
import { httpCatalogApi, mockCatalogApi } from './features/catalog';
import { httpCrmApi, mockCrmApi } from './features/crm';
import { mockDashboardApi } from './features/dashboard';
import { httpDeparturesApi, mockDeparturesApi } from './features/departures';
import { mockSearchApi } from './features/search';
import { createHttpBookingFlowApi, mockBookingFlowApi } from './features/booking';
import { createHttpBookingsApi, mockBookingsApi } from './features/bookings';
import { createHttpSubAgentsApi, mockSubAgentsApi } from './features/subagents';
import { mockWalletApi } from './features/wallet';
import { createHttpBillingApi, mockBillingApi } from './features/billing';
import './index.css';

/**
 * Which adapter stands behind each port. The one place to change when a mock
 * is replaced by a real endpoint:
 *
 *   auth       real `/api/v1/auth` by default; `VITE_AUTH_MODE=mock` for demos
 *   wallet     mock until the statement endpoints exist (#26 shipped top-ups)
 *   dashboard  mock until the orders endpoints exist (#41, #42)
 *   search     mock until the supplier search endpoints exist (#33, #34)
 *   bookingFlow  real `/api/v1/bookings` (#42); the stand-in with `VITE_AUTH_MODE=mock`
 *   bookings     real `/api/v1/bookings` (#42, #44); the stand-in with `VITE_AUTH_MODE=mock`
 *   analytics    real `/api/v1/analytics` and `/api/v1/reports` (issues 67, 68); the stand-in
 *                with `VITE_AUTH_MODE=mock`
 *   catalog      real `/api/v1/catalog`; the stand-in with `VITE_AUTH_MODE=mock`
 *   departures   real `/api/v1/catalog`; the stand-in with `VITE_AUTH_MODE=mock`
 *   crm          real `/api/v1/crm`; the stand-in with `VITE_AUTH_MODE=mock`
 *   billing      real `/api/v1/billing` (issues 64, 65); the stand-in with `VITE_AUTH_MODE=mock`
 *   subAgents    real `/api/v1/sub-agents` (issue 63); the stand-in with `VITE_AUTH_MODE=mock`
 */
const adapters: AppAdapters = {
  analytics:
    AUTH_MODE === 'mock'
      ? mockAnalyticsApi
      : createHttpAnalyticsApi({ api, send: sessionTransport.authFetch }),
  auth: AUTH_MODE === 'mock' ? mockAuthApi : createHttpAuthApi({ api, publicApi }),
  wallet: mockWalletApi,
  dashboard: mockDashboardApi,
  search: mockSearchApi,
  bookingFlow: AUTH_MODE === 'mock' ? mockBookingFlowApi : createHttpBookingFlowApi({ api }),
  bookings: AUTH_MODE === 'mock' ? mockBookingsApi : createHttpBookingsApi({ api }),
  catalog: AUTH_MODE === 'mock' ? mockCatalogApi : httpCatalogApi,
  departures: AUTH_MODE === 'mock' ? mockDeparturesApi : httpDeparturesApi,
  crm: AUTH_MODE === 'mock' ? mockCrmApi : httpCrmApi,
  billing: AUTH_MODE === 'mock' ? mockBillingApi : createHttpBillingApi({ api }),
  subAgents: AUTH_MODE === 'mock' ? mockSubAgentsApi : createHttpSubAgentsApi({ api }),
};

const queryClient = createQueryClient();
const router = createAppRouter();

const root = document.getElementById('root');
if (!root) throw new Error('Root element not found');

createRoot(root).render(
  <StrictMode>
    <AppProviders queryClient={queryClient} adapters={adapters}>
      <RouterProvider router={router} />
    </AppProviders>
  </StrictMode>,
);
