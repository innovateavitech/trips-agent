import { createBrowserRouter } from 'react-router-dom';
import { RequireAnonymous, RequireAuth } from './auth/require-auth';
import { walletRoutes } from './features/wallet';
import { AppShell } from './layout/app-shell';
import { DashboardPage } from './pages/dashboard';
import { LoginPage } from './pages/login';
import { NotFoundPage } from './pages/not-found';
import { RouteErrorPage } from './pages/route-error';

/**
 * Two route groups, as issue #48 requires.
 *
 *   PUBLIC          reachable signed out — login, register, forgot password (#49)
 *   AUTHENTICATED   everything behind `RequireAuth`, inside the app shell
 *
 * Adding a screen? Put it in the right group. A feature route added at the top
 * level is a route with NO auth guard, and on this product that is a data leak,
 * not a styling bug.
 *
 * Feature routes arrive as `RouteObject[]` exported from the feature — see
 * `features/wallet/routes.tsx` — and are spread into the authenticated group.
 * The feature owns its paths; this file owns who may reach them.
 */
export const router = createBrowserRouter([
  {
    element: <RequireAnonymous />,
    errorElement: <RouteErrorPage />,
    children: [
      { path: '/login', element: <LoginPage /> },
      // #49 also adds /register, /verify-email and /forgot-password here.
    ],
  },
  {
    element: <RequireAuth />,
    errorElement: <RouteErrorPage />,
    children: [
      {
        element: <AppShell />,
        children: [
          { index: true, element: <DashboardPage /> },

          // Wallet and statement screens (#51), landed in #86. Until now nothing
          // routed to them, so they shipped unreachable.
          ...walletRoutes,

          // Still to come: /search (#52), /bookings (#54), /catalog, /settings (#55).
          // An unknown path falls through to the 404 below, inside the shell.
          { path: '*', element: <NotFoundPage /> },
        ],
      },
    ],
  },
]);
