import type { RouteObject } from 'react-router-dom';
import { ForgotPasswordPage } from './pages/forgot-password-page';
import { RegisterPage } from './pages/register-page';
import { ResetPasswordPage } from './pages/reset-password-page';
import { VerifyEmailPage } from './pages/verify-email-page';

/**
 * Registration, email verification and password reset (#49).
 *
 * Public routes: they render in `PublicLayout`, outside the auth guard — nobody
 * arriving here has a session, and two of them are reached from a link in an
 * email. Sign-in itself lives in `src/auth/pages`, because the guard owns it.
 *
 * Not behind `RedirectIfSignedIn`, unlike sign-in. A signed-in agent who opens
 * a reset link from their inbox should be able to use it; bouncing them to the
 * dashboard would make the link look broken.
 */
export const authRoutes: RouteObject[] = [
  { path: '/register', element: <RegisterPage /> },
  { path: '/verify-email', element: <VerifyEmailPage /> },
  { path: '/forgot-password', element: <ForgotPasswordPage /> },
  { path: '/reset-password', element: <ResetPasswordPage /> },
];
