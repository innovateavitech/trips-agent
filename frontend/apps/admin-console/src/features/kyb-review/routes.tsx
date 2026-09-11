import type { RouteObject } from 'react-router-dom';
import { KYB_REVIEW_PERMISSION } from '../../lib/auth/claims';
import { RequirePermission } from '../auth/guards';
import { KybQueuePage } from './pages/queue-page';
import { KybSubmissionPage } from './pages/submission-page';

/**
 * The feature's routes, exported as data rather than as a `<Routes>` tree this feature owns, so
 * the app's router can mount them inside its authenticated group — the same hand-over the wallet
 * feature uses in the agent console.
 */
const REFUSAL = {
  title: 'Your account cannot review KYB submissions',
  detail:
    'Reviewing an agency needs the kyb.review permission, which Operations Admins and Super Admins hold. Ask a Super Admin if you need it.',
};

export const kybReviewRoutes: RouteObject[] = [
  {
    path: '/kyb',
    element: (
      <RequirePermission permission={KYB_REVIEW_PERMISSION} refusal={REFUSAL}>
        <KybQueuePage />
      </RequirePermission>
    ),
  },
  {
    path: '/kyb/:submissionId',
    element: (
      <RequirePermission permission={KYB_REVIEW_PERMISSION} refusal={REFUSAL}>
        <KybSubmissionPage />
      </RequirePermission>
    ),
  },
];
