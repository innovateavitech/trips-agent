import { QueryClientProvider, type QueryClient } from '@tanstack/react-query';
import { Navigate, RouterProvider, createBrowserRouter } from 'react-router-dom';
import { Card, buttonVariants } from '@trips/ui';
import { Link } from 'react-router-dom';
import { AppShell } from './app/app-shell';
import { EmptyState } from './components/states';
import { AuthProvider } from './features/auth/auth-context';
import { RequireStaff } from './features/auth/guards';
import { SignInPage } from './features/auth/sign-in-page';
import { KybReviewApiProvider, kybReviewRoutes, type KybReviewApi } from './features/kyb-review';
import type { ApiClient } from './lib/api/client';
import { HOME_PATH } from './lib/auth/redirect';
import type { SessionStore } from './lib/auth/session-store';

/**
 * Two route groups: the public sign-in page, and everything else behind `RequireStaff`.
 *
 * The router is built once at module level, not inside the component — rebuilding it on a render
 * would remount every screen and throw away the page's state.
 */
const router = createBrowserRouter([
  { path: '/sign-in', element: <SignInPage /> },
  {
    element: (
      <RequireStaff>
        <AppShell />
      </RequireStaff>
    ),
    children: [
      { index: true, element: <Navigate to={HOME_PATH} replace /> },
      ...kybReviewRoutes,
      { path: '*', element: <NotFoundPage /> },
    ],
  },
]);

export function App({
  client,
  store,
  kybReviewApi,
  queryClient,
}: {
  client: ApiClient;
  store: SessionStore;
  kybReviewApi: KybReviewApi;
  queryClient: QueryClient;
}) {
  return (
    <QueryClientProvider client={queryClient}>
      <AuthProvider client={client} store={store}>
        <KybReviewApiProvider value={kybReviewApi}>
          <RouterProvider router={router} />
        </KybReviewApiProvider>
      </AuthProvider>
    </QueryClientProvider>
  );
}

function NotFoundPage() {
  return (
    <div className="mx-auto w-full max-w-3xl px-4 py-10 sm:px-6">
      <Card>
        <EmptyState
          title="There is nothing at this address"
          action={
            <Link to={HOME_PATH} className={buttonVariants({ variant: 'outline' })}>
              Go to KYB review
            </Link>
          }
        >
          The link may be out of date, or the screen may not be built yet — the rest of the back
          office arrives with epic #66.
        </EmptyState>
      </Card>
    </div>
  );
}
