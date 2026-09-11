import type { RouteObject } from 'react-router-dom';
import { VerificationPage } from './pages/verification-page';

/** Issue #50 — onboarding and KYB document upload. */
export const onboardingRoutes: RouteObject[] = [
  {
    path: '/verification',
    element: <VerificationPage />,
  },
];
