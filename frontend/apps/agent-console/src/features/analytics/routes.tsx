import type { RouteObject } from 'react-router-dom';
import { AnalyticsPage } from './pages/analytics-page';
import { ReportsPage } from './pages/reports-page';

/**
 * The analytics screens, handed to `app/router.tsx` as data like every other
 * feature's.
 *
 * No permission guard here. Both endpoints behind these pages require
 * `report.view`, and the API is what enforces it — a client-side guard would be
 * a second, weaker copy of the same rule that could drift from it.
 */
export const analyticsRoutes: RouteObject[] = [
  { path: '/analytics', element: <AnalyticsPage /> },
  { path: '/reports', element: <ReportsPage /> },
];
