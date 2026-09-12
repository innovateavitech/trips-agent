import { QueryClientProvider, type QueryClient } from '@tanstack/react-query';
import { Navigate, RouterProvider, createBrowserRouter, Link, useNavigate } from 'react-router-dom';
import { Card, buttonVariants } from '@trips/ui';
import { AppShell } from './app/app-shell';
import { EmptyState } from './components/states';
import { AuthProvider, useAuth } from './features/auth/auth-context';
import { RequireStaff } from './features/auth/guards';
import { SignInPage } from './features/auth/sign-in-page';
import { AgenciesApiProvider, agencyRoutes, type AgenciesApi } from './features/agencies';
import { AuditApiProvider, auditRoutes, type AuditApi } from './features/audit';
import {
  BackOfficeApiProvider,
  backOfficeRoutes,
  type BackOfficeApi,
} from './features/back-office';
import { DashboardApiProvider, dashboardRoutes, type DashboardApi } from './features/dashboard';
import { KybReviewApiProvider, kybReviewRoutes, type KybReviewApi } from './features/kyb-review';
import type { ApiClient } from './lib/api/client';
import { homePathFor } from './lib/auth/redirect';
import type { SessionStore } from './lib/auth/session-store';

/**
 * Two route groups: the public sign-in page, and everything else behind `RequireStaff`.
 *
 * The router is built once at module level, not inside the component — rebuilding it on a render
 * would remount every screen and throw away the page's state.
 *
 * Each feature hands over its routes as data rather than owning a `<Routes>` tree of its own, so
 * every screen in the console sits inside the one authenticated group and no feature can
 * accidentally mount itself outside it.
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
      { index: true, element: <HomeRedirect /> },
      ...dashboardRoutes,
      ...agencyRoutes,
      ...kybReviewRoutes,
      ...auditRoutes,
      ...backOfficeRoutes,
      { path: '*', element: <NotFoundPage /> },
    ],
  },
]);

/**
 * Everything the console needs from the server, built once in `main.tsx` and passed in here.
 *
 * One provider per feature rather than one big context: a test can hand a single feature a fake
 * without having to build the other four.
 */
export interface ConsoleApis {
  agencies: AgenciesApi;
  audit: AuditApi;
  backOffice: BackOfficeApi;
  dashboard: DashboardApi;
  kybReview: KybReviewApi;
}

export function App({
  client,
  store,
  apis,
  queryClient,
}: {
  client: ApiClient;
  store: SessionStore;
  apis: ConsoleApis;
  queryClient: QueryClient;
}) {
  return (
    <QueryClientProvider client={queryClient}>
      <AuthProvider client={client} store={store}>
        <AgenciesApiProvider value={apis.agencies}>
          <AuditApiProvider value={apis.audit}>
            <BackOfficeApiProvider value={apis.backOffice}>
              <DashboardApiProvider value={apis.dashboard}>
                <KybReviewApiProvider value={apis.kybReview}>
                  <RouterProvider router={router} />
                </KybReviewApiProvider>
              </DashboardApiProvider>
            </BackOfficeApiProvider>
          </AuditApiProvider>
        </AgenciesApiProvider>
      </AuthProvider>
    </QueryClientProvider>
  );
}

/**
 * The root path, sent on to whichever screen this account is actually allowed to open.
 *
 * A fixed home page would land a Support account on a refusal at every sign-in: they hold
 * `agency.view` and nothing else.
 */
function HomeRedirect() {
  const { session } = useAuth();
  return <Navigate to={homePathFor(session?.claims)} replace />;
}

function NotFoundPage() {
  const { session } = useAuth();
  const navigate = useNavigate();
  const home = homePathFor(session?.claims);

  return (
    <div className="mx-auto w-full max-w-3xl px-4 py-10 sm:px-6">
      <Card>
        <EmptyState
          title="There is nothing at this address"
          action={
            <div className="flex flex-wrap justify-center gap-2">
              <button
                type="button"
                className={buttonVariants({ variant: 'outline' })}
                onClick={() => void navigate(-1)}
              >
                Go back
              </button>
              <Link to={home} className={buttonVariants({ variant: 'primary' })}>
                Start again
              </Link>
            </div>
          }
        >
          The link may be out of date, or the screen may need a permission your account does not
          hold — the console only shows what your role can open.
        </EmptyState>
      </Card>
    </div>
  );
}
