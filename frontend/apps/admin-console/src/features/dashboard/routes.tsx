import type { RouteObject } from 'react-router-dom';
import { PLATFORM_REPORT_PERMISSION } from '../../lib/auth/claims';
import { RequirePermission } from '../auth/guards';
import { DashboardPage } from './pages/dashboard-page';

const REFUSAL = {
  title: 'Your account cannot open the dashboard',
  detail:
    'The platform-wide numbers need the platform.report.view permission. Ask a Super Admin if ' +
    'you need it.',
};

export const dashboardRoutes: RouteObject[] = [
  {
    path: '/dashboard',
    element: (
      <RequirePermission permission={PLATFORM_REPORT_PERMISSION} refusal={REFUSAL}>
        <DashboardPage />
      </RequirePermission>
    ),
  },
];
