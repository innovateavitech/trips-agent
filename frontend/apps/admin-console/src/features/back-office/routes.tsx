import type { RouteObject } from 'react-router-dom';
import { PLATFORM_USER_PERMISSION } from '../../lib/auth/claims';
import { RequirePermission } from '../auth/guards';
import { BackOfficeUsersPage } from './pages/users-page';

/**
 * Behind `platform.user.manage`, which only a Super Admin holds. Operations, Support and Finance
 * are all refused here — granting somebody the ability to suspend a customer is not one of their
 * jobs.
 */
const REFUSAL = {
  title: 'Your account cannot manage back-office users',
  detail:
    'Creating accounts and changing roles needs the platform.user.manage permission, which only ' +
    'a Super Admin holds. Ask one of them.',
};

export const backOfficeRoutes: RouteObject[] = [
  {
    path: '/users',
    element: (
      <RequirePermission permission={PLATFORM_USER_PERMISSION} refusal={REFUSAL}>
        <BackOfficeUsersPage />
      </RequirePermission>
    ),
  },
];
