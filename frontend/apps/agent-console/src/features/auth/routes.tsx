import type { RouteObject } from 'react-router-dom';
import { KeyRound, MailCheck, UserPlus } from 'lucide-react';
import { PlannedScreen } from '../../shell/planned-screen';

/**
 * Issue #49 — registration, email verification and password reset.
 *
 * Public routes: they render in `PublicLayout`, outside the auth guard. Sign-in
 * itself is finished and lives in `src/auth/pages`. Replace each placeholder
 * with the real page; nothing outside this folder needs to change.
 */
export const authRoutes: RouteObject[] = [
  {
    path: '/register',
    element: (
      <PlannedScreen
        frame="public"
        title="Register your agency"
        description="Create an agent account for your travel business."
        issue={49}
        icon={UserPlus}
        capabilities={[
          'Register with your business name, your name, email and phone',
          'Get a six-digit code by email to confirm the address',
          'Go straight on to business verification once confirmed',
        ]}
      />
    ),
  },
  {
    path: '/verify-email',
    element: (
      <PlannedScreen
        frame="public"
        title="Confirm your email"
        description="Enter the code we sent to your inbox."
        issue={49}
        icon={MailCheck}
        capabilities={['Enter the six-digit code', 'Ask for a new code if it has expired']}
      />
    ),
  },
  {
    path: '/forgot-password',
    element: (
      <PlannedScreen
        frame="public"
        title="Reset your password"
        description="We will email you a link that works once, for 30 minutes."
        issue={49}
        icon={KeyRound}
        capabilities={[
          'Ask for a reset link by email',
          'Choose a new password from the link, which signs out every other device',
        ]}
      />
    ),
  },
  {
    path: '/reset-password',
    element: (
      <PlannedScreen
        frame="public"
        title="Choose a new password"
        description="Set the password you will sign in with from now on."
        issue={49}
        icon={KeyRound}
        capabilities={['Choose and confirm a new password']}
      />
    ),
  },
];
