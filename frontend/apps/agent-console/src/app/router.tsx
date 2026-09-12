import { createBrowserRouter, type RouteObject } from 'react-router-dom';
import { RedirectIfSignedIn, RequireAuth } from '../auth/guards';
import { SignInPage } from '../auth/pages/sign-in-page';
import { SIGN_IN_PATH } from '../auth/redirect';
import { authRoutes } from '../features/auth/routes';
import { bookingRoutes } from '../features/booking/routes';
import { bookingsRoutes } from '../features/bookings/routes';
import { catalogRoutes } from '../features/catalog/routes';
import { crmRoutes } from '../features/crm/routes';
import { departuresRoutes } from '../features/departures/routes';
import { DashboardPage } from '../features/dashboard';
import { onboardingRoutes } from '../features/onboarding/routes';
import { pricingRoutes } from '../features/pricing/routes';
import { searchRoutes } from '../features/search/routes';
import { storefrontRoutes } from '../features/storefront/routes';
import { walletRoutes } from '../features/wallet';
import { AppShell } from '../shell/app-shell';
import { PublicLayout } from '../shell/public-layout';
import { NotFoundPage, RouteErrorPage } from '../shell/route-error-page';

/**
 * ============================================================================
 *  Every route in the console, in two groups.
 * ============================================================================
 *
 *  PUBLIC         rendered in `PublicLayout`, reachable signed out
 *    /sign-in     — and bounced onward by `RedirectIfSignedIn` if signed in
 *    /register, /verify-email, /forgot-password, /reset-password   (#49)
 *
 *  AUTHENTICATED  behind `RequireAuth`, rendered in `AppShell`
 *    /                      dashboard
 *    /search/flights|buses  (#52)       /book/*        (#53)
 *    /bookings[/:id]        (#54)       /resolution    (#54)
 *    /wallet/…              (#51)       /pricing       (#55)
 *    /website/…             (#58, #59)
 *    /verification          (#50)
 *
 * Each feature owns its `routes.tsx` and hands over an array; this file only
 * decides which group it joins. To add a screen, add it to the feature's array
 * — and to `navigation.ts` if it belongs in the sidebar.
 */
export const appRoutes: RouteObject[] = [
  {
    errorElement: <RouteErrorPage />,
    children: [
      {
        element: <PublicLayout />,
        children: [
          {
            element: <RedirectIfSignedIn />,
            children: [{ path: SIGN_IN_PATH, element: <SignInPage /> }],
          },
          ...authRoutes,
        ],
      },
      {
        element: <RequireAuth />,
        children: [
          {
            element: <AppShell />,
            children: [
              { index: true, element: <DashboardPage /> },
              ...searchRoutes,
              ...bookingRoutes,
              ...bookingsRoutes,
              ...catalogRoutes,
              ...departuresRoutes,
              ...crmRoutes,
              ...walletRoutes,
              ...pricingRoutes,
              ...storefrontRoutes,
              ...onboardingRoutes,
              { path: '*', element: <NotFoundPage /> },
            ],
          },
        ],
      },
    ],
  },
];

export function createAppRouter() {
  return createBrowserRouter(appRoutes);
}
