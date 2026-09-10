import { EmptyState } from '@trips/ui';
import { useAuth } from '../auth/auth-context';

/**
 * Landing screen after sign-in.
 *
 * Intentionally thin. Issue #48 builds the SHELL; the widgets that belong here
 * (wallet balance, recent bookings, expiring ticket time limits) depend on
 * endpoints that do not exist yet — see #51 and #54.
 */
export function DashboardPage() {
  const { session } = useAuth();
  const firstName = session?.user.fullName.split(' ')[0] ?? 'there';

  return (
    <div className="flex flex-col gap-6">
      <div className="flex flex-col gap-1">
        <h1 className="text-2xl font-semibold tracking-tight text-foreground">
          Welcome back, {firstName}
        </h1>
        <p className="text-sm text-muted-foreground">{session?.activeAgency.name}</p>
      </div>

      <EmptyState
        title="Nothing to show yet"
        description="Wallet balance, recent bookings and expiring ticket time limits appear here once those screens are built."
      />
    </div>
  );
}
