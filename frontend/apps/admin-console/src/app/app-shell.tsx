import { NavLink, Outlet } from 'react-router-dom';
import { cn } from '@trips/ui';
import { BrandMark } from '../components/brand-mark';
import { QueueIcon } from '../components/icons';
import { useAuth } from '../features/auth/auth-context';
import { useKybQueue } from '../features/kyb-review/kyb-review-queries';
import { KYB_REVIEW_PERMISSION, hasPermission } from '../lib/auth/claims';
import { UserMenu } from './user-menu';

/**
 * The frame around every signed-in screen: navigation on the left, the account in the top right.
 *
 * Only screens that exist are listed. A sidebar full of "coming soon" entries teaches staff to
 * ignore the sidebar; the rest of epic #66 adds its entries here as each screen lands.
 */
export function AppShell() {
  return (
    <div className="flex min-h-screen bg-background">
      <aside className="sticky top-0 hidden h-screen w-60 shrink-0 flex-col gap-8 border-r border-border bg-muted px-3 py-5 lg:flex">
        <div className="px-2">
          <BrandMark />
        </div>
        <nav aria-label="Main" className="flex flex-col gap-1">
          <NavItems />
        </nav>
        <p className="mt-auto px-2 text-xs leading-relaxed text-muted-foreground">
          Staff only. Every decision is recorded in the audit log with your name.
        </p>
      </aside>

      <div className="flex min-w-0 flex-1 flex-col">
        <header className="flex h-16 items-center gap-4 border-b border-border px-4 sm:px-6">
          <div className="flex min-w-0 items-center gap-4 lg:hidden">
            <BrandMark />
            <nav aria-label="Main" className="flex gap-1">
              <NavItems />
            </nav>
          </div>
          <div className="ml-auto">
            <UserMenu />
          </div>
        </header>

        <main className="flex-1">
          <Outlet />
        </main>
      </div>
    </div>
  );
}

function NavItems() {
  const { session } = useAuth();
  if (!session) return null;

  return hasPermission(session.claims, KYB_REVIEW_PERMISSION) ? <KybNavItem /> : null;
}

/** Its own component so the queue is only fetched for accounts allowed to read it. */
function KybNavItem() {
  const queue = useKybQueue();
  const waiting = queue.data?.length ?? 0;

  return (
    <NavLink
      to="/kyb"
      className={({ isActive }) =>
        cn(
          'flex items-center gap-2.5 rounded-md px-2.5 py-2 text-sm transition-colors',
          isActive
            ? 'bg-primary-subtle font-medium text-primary'
            : 'text-muted-foreground hover:bg-accent hover:text-accent-foreground',
        )
      }
    >
      <QueueIcon />
      <span className="flex-1 whitespace-nowrap">KYB review</span>
      {waiting > 0 ? (
        <span className="rounded-full bg-primary px-1.5 text-xs font-medium tabular-nums text-primary-foreground">
          {waiting}
          <span className="sr-only"> waiting</span>
        </span>
      ) : null}
    </NavLink>
  );
}
