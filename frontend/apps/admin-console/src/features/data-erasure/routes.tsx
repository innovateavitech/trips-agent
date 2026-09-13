import type { RouteObject } from 'react-router-dom';
import { ERASURE_EXECUTE_PERMISSION } from '../../lib/auth/claims';
import { RequirePermission } from '../auth/guards';
import { DataErasurePage } from './pages/erasure-page';

/**
 * Behind `platform.erasure.execute`, which only a Super Admin holds.
 *
 * It is the one screen in the console that destroys something for good, so it is deliberately not
 * bundled with the permissions somebody needs to answer a question about a booking.
 */
const REFUSAL = {
  title: 'Your account cannot erase a person',
  detail:
    'Erasing somebody needs the platform.erasure.execute permission, which only a Super Admin ' +
    'holds. It cannot be undone, which is why it sits on its own.',
};

export const erasureRoutes: RouteObject[] = [
  {
    path: '/data-erasure',
    element: (
      <RequirePermission permission={ERASURE_EXECUTE_PERMISSION} refusal={REFUSAL}>
        <DataErasurePage />
      </RequirePermission>
    ),
  },
];
