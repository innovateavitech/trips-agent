import type { RouteObject } from 'react-router-dom';
import { BillingPage } from './pages/billing-page';
import { BillingReturnPage } from './pages/billing-return-page';

export const billingRoutes: RouteObject[] = [
  { path: '/billing', element: <BillingPage /> },

  // Where Paystack redirects after a hosted-page payment. A route of its own rather than a query
  // parameter on /billing, so a bookmarked return link cannot re-ask about an old payment every
  // time somebody opens their billing screen.
  { path: '/billing/return', element: <BillingReturnPage /> },
];
