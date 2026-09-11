import type { ReactNode } from 'react';
import { Navigate, useLocation, useNavigate } from 'react-router-dom';
import { Button, Card } from '@trips/ui';
import { EmptyState, FullPageLoading } from '../../components/states';
import { hasPermission, isTripsStaff } from '../../lib/auth/claims';
import { useAuth } from './auth-context';

/**
 * Everything inside the app shell sits behind this.
 *
 * It sends a signed-out visitor to sign in, carrying the page they asked for so they land back
 * on it — a reviewer who follows a link to one submission should arrive at that submission.
 *
 * This decides what the browser renders. The API decides what the browser is allowed to *read*,
 * and it checks every request on its own.
 */
export function RequireStaff({ children }: { children: ReactNode }) {
  const { status, session } = useAuth();
  const location = useLocation();

  if (status === 'restoring') return <FullPageLoading label="Signing you back in" />;

  if (!session) {
    return (
      <Navigate to="/sign-in" replace state={{ from: `${location.pathname}${location.search}` }} />
    );
  }

  // Sign-in already refuses agency accounts; this catches one that got here some other way.
  if (!isTripsStaff(session.claims)) return <NotStaffScreen />;

  return <>{children}</>;
}

/** Wraps one screen that needs a particular permission, e.g. `kyb.review` for the queue. */
export function RequirePermission({
  permission,
  children,
  refusal,
}: {
  permission: string;
  children: ReactNode;
  /** What this account cannot do, and who can fix it. */
  refusal: { title: string; detail: string };
}) {
  const { session } = useAuth();

  if (!session || !hasPermission(session.claims, permission)) {
    return (
      <div className="mx-auto w-full max-w-3xl px-4 py-10 sm:px-6">
        <Card>
          <EmptyState title={refusal.title}>{refusal.detail}</EmptyState>
        </Card>
      </div>
    );
  }

  return <>{children}</>;
}

function NotStaffScreen() {
  const { signOut } = useAuth();
  const navigate = useNavigate();

  return (
    <main className="flex min-h-screen items-center justify-center bg-muted px-4">
      <Card className="w-full max-w-md">
        <EmptyState
          title="This console is for Trips staff"
          action={
            <Button
              variant="outline"
              onClick={() => {
                void signOut().then(() => navigate('/sign-in', { replace: true }));
              }}
            >
              Sign out
            </Button>
          }
        >
          You are signed in with a travel agency account. Agencies use the agent console.
        </EmptyState>
      </Card>
    </main>
  );
}
