import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { QueryClient } from '@tanstack/react-query';
import { App, type ConsoleApis } from './App';
import { createHttpAgenciesApi } from './features/agencies';
import { createHttpAuditApi } from './features/audit';
import { createHttpBackOfficeApi } from './features/back-office';
import { createHttpDashboardApi } from './features/dashboard';
import { createHttpKybReviewApi } from './features/kyb-review';
import { createApiClient } from './lib/api/client';
import { ApiError } from './lib/api/problem';
import { browserSessionStorage, createSessionStore } from './lib/auth/session-store';
import './index.css';

/**
 * The composition root: everything the app depends on is built here and passed in, so no module
 * reaches for a singleton and a test can build the same app with a fake API.
 */
const store = createSessionStore(browserSessionStorage());
const client = createApiClient({ store });

const apis: ConsoleApis = {
  agencies: createHttpAgenciesApi(client),
  audit: createHttpAuditApi(client),
  backOffice: createHttpBackOfficeApi(client),
  dashboard: createHttpDashboardApi(client),
  kybReview: createHttpKybReviewApi(client),
};

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      // Retrying a 401, 403 or 404 just repeats a refusal the server has already thought about.
      // Only genuine server trouble is worth a second attempt.
      retry: (failureCount, error) =>
        !(error instanceof ApiError && error.status < 500) && failureCount < 2,
      staleTime: 15_000,
    },
    mutations: {
      // Never retry a decision. Approving twice is merely noisy, but an automatic retry after a
      // timeout can send a rejection the reviewer believes failed. See decision-rules.ts.
      retry: false,
    },
  },
});

const root = document.getElementById('root');
if (!root) throw new Error('Root element not found');

createRoot(root).render(
  <StrictMode>
    <App client={client} store={store} apis={apis} queryClient={queryClient} />
  </StrictMode>,
);
