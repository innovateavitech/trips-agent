import { useState } from 'react';
import { QueryClientProvider } from '@tanstack/react-query';
import { RouterProvider } from 'react-router-dom';
import { createQueryClient } from './api/query-client';
import { AuthProvider } from './auth/auth-context';
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
        <RouterProvider router={router} />
      </AuthProvider>
    </QueryClientProvider>
  );
}
