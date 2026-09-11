import type { RouteObject } from 'react-router-dom';
import { ShieldCheck } from 'lucide-react';
import { PlannedScreen } from '../../shell/planned-screen';

/** Issue #50 — onboarding and KYB document upload. */
export const onboardingRoutes: RouteObject[] = [
  {
    path: '/verification',
    element: (
      <PlannedScreen
        title="Business verification"
        description="Verify your agency so you can take payments and issue tickets."
        issue={50}
        icon={ShieldCheck}
        capabilities={[
          'Upload your CAC certificate, director ID and proof of address',
          'Track the review, and see exactly what to fix if a document is rejected',
          'See the limits that apply until verification is complete',
        ]}
      />
    ),
  },
];
