import type { RouteObject } from 'react-router-dom';
import { PLATFORM_REPORT_PERMISSION } from '../../lib/auth/claims';
import { RequirePermission } from '../auth/guards';
import { PlatformAnalyticsPage } from './pages/platform-analytics-page';

const REFUSAL = {
  title: 'Your account cannot open platform analytics',
  detail:
    'GMV, growth and supplier performance cross every agency, so they need the ' +
    'platform.report.view permission. Ask a Super Admin if you need it.',
};

export const platformAnalyticsRoutes: RouteObject[] = [
  {
    path: '/analytics',
    element: (
      <RequirePermission permission={PLATFORM_REPORT_PERMISSION} refusal={REFUSAL}>
        <PlatformAnalyticsPage />
      </RequirePermission>
    ),
  },
];
