import type { RouteObject } from 'react-router-dom';
import { AGENCY_VIEW_PERMISSION } from '../../lib/auth/claims';
import { RequirePermission } from '../auth/guards';
import { AgencyDirectoryPage } from './pages/directory-page';
import { AgencyProfilePage } from './pages/profile-page';

/**
 * The feature's routes, exported as data rather than as a `<Routes>` tree this feature owns, so
 * the app's router can mount them inside its authenticated group — the same hand-over the KYB
 * review feature uses.
 *
 * Both are behind `agency.view`, which every back-office role holds. The narrower permissions —
 * editing, suspending, terminating, exporting — are checked per control inside the screens, so
 * Support can read an agency's profile without being offered buttons it cannot press.
 */
const REFUSAL = {
  title: 'Your account cannot open the agency directory',
  detail:
    'Reading an agency needs the agency.view permission, which every back-office role holds. ' +
    'Ask a Super Admin if you need it.',
};

export const agencyRoutes: RouteObject[] = [
  {
    path: '/agencies',
    element: (
      <RequirePermission permission={AGENCY_VIEW_PERMISSION} refusal={REFUSAL}>
        <AgencyDirectoryPage />
      </RequirePermission>
    ),
  },
  {
    path: '/agencies/:agencyId',
    element: (
      <RequirePermission permission={AGENCY_VIEW_PERMISSION} refusal={REFUSAL}>
        <AgencyProfilePage />
      </RequirePermission>
    ),
  },
];
