import type { RouteObject } from 'react-router-dom';
import { DisputeDetailPage, DisputesPage } from './pages/disputes-page';
import { PayoutsPage } from './pages/payouts-page';

/** Build plan F12 — withdrawals, bank accounts and chargebacks. */
export const payoutRoutes: RouteObject[] = [
  { path: '/payouts', element: <PayoutsPage /> },
  { path: '/disputes', element: <DisputesPage /> },
  { path: '/disputes/:disputeId', element: <DisputeDetailPage /> },
];
