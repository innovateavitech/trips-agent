import type { RouteObject } from 'react-router-dom';
import { WalletPage } from './pages/wallet-page';
import { TopUpReturnPage } from './pages/top-up-return-page';
import { MockGatewayPage } from './mock/mock-gateway-page';

/**
 * The wallet's routes, exported as data rather than as JSX.
 *
 * Issue #48 builds the real router and app shell. When it lands it can spread
 * this array into its authenticated route group and delete nothing — which is
 * the point of handing it over as a value instead of a `<Routes>` tree this
 * feature owns.
 *
 * Paths are relative-free (absolute) so they read the same wherever they are
 * mounted from.
 */
export const walletRoutes: RouteObject[] = [
  { path: '/wallet', element: <WalletPage /> },
  { path: '/wallet/top-up/return', element: <TopUpReturnPage /> },

  // Delete with the rest of the mock when issue #26 lands.
  { path: '/wallet/top-up/mock-gateway', element: <MockGatewayPage /> },
];
