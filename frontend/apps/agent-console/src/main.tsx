import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { RouterProvider } from 'react-router-dom';
import { api } from './api/client';
import { publicApi } from './api/public-client';
import { AppProviders, type AppAdapters } from './app/providers';
import { createQueryClient } from './app/query-client';
import { createAppRouter } from './app/router';
import { AUTH_MODE } from './app/settings';
import { createHttpAuthApi } from './auth/http-auth-api';
import { mockAuthApi } from './auth/mock/mock-auth-api';
import { mockDashboardApi } from './features/dashboard';
import { mockWalletApi } from './features/wallet';
import './index.css';

/**
 * Which adapter stands behind each port. The one place to change when a mock
 * is replaced by a real endpoint:
 *
 *   auth       real `/api/v1/auth` by default; `VITE_AUTH_MODE=mock` for demos
 *   wallet     mock until the statement endpoints exist (#26 shipped top-ups)
 *   dashboard  mock until the orders endpoints exist (#41, #42)
 */
const adapters: AppAdapters = {
  auth: AUTH_MODE === 'mock' ? mockAuthApi : createHttpAuthApi({ api, publicApi }),
  wallet: mockWalletApi,
  dashboard: mockDashboardApi,
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
