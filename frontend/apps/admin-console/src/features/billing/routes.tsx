import type { RouteObject } from 'react-router-dom';
import { SUBSCRIPTION_PERMISSION } from '../../lib/auth/claims';
import { RequirePermission } from '../auth/guards';
import { SubscribersPage } from './pages/subscribers-page';
import { TiersPage } from './pages/tiers-page';

/**
 * Behind `subscription.manage`, which a Super Admin and Finance hold.
 *
 * Operations and Support are refused: what Trips charges its customers is a commercial decision,
 * and a support conversation about a bill ends in a finance ticket rather than in a price change.
 */
const REFUSAL = {
  title: 'Your account cannot manage plans',
  detail:
    'Setting what Trips charges needs the subscription.manage permission, which a Super Admin and ' +
    'Finance hold. Ask one of them.',
};

export const billingRoutes: RouteObject[] = [
  {
    path: '/plans',
    element: (
      <RequirePermission permission={SUBSCRIPTION_PERMISSION} refusal={REFUSAL}>
        <TiersPage />
      </RequirePermission>
    ),
  },
  {
    path: '/subscribers',
    element: (
      <RequirePermission permission={SUBSCRIPTION_PERMISSION} refusal={REFUSAL}>
        <SubscribersPage />
      </RequirePermission>
    ),
  },
];
