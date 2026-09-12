import type { ReactNode } from 'react';
import { NavLink, Outlet } from 'react-router-dom';
import { cn } from '@trips/ui';
import { BrandMark } from '../components/brand-mark';
import {
  BuildingIcon,
  DashboardIcon,
  HistoryIcon,
  PeopleIcon,
  QueueIcon,
} from '../components/icons';
import { useAuth } from '../features/auth/auth-context';
import { useKybQueue } from '../features/kyb-review/kyb-review-queries';
import {
  AGENCY_VIEW_PERMISSION,
  AUDIT_VIEW_PERMISSION,
  KYB_REVIEW_PERMISSION,
  PLATFORM_REPORT_PERMISSION,
  PLATFORM_USER_PERMISSION,
  hasPermission,
} from '../lib/auth/claims';
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

/**
 * The sidebar, in the order the work happens: the numbers, then the customers, then the queue
 * waiting on somebody, then the record of what was done, then the accounts that did it.
 *
 * Only screens this account can actually open are listed. A sidebar full of entries that answer
 * "you cannot open this" teaches staff to ignore the sidebar — and a Support account holds
 * `agency.view` alone, so for them this is a list of one.
 */
const NAV: { to: string; label: string; permission: string; icon: ReactNode }[] = [
  {
    to: '/dashboard',
    label: 'Dashboard',
    permission: PLATFORM_REPORT_PERMISSION,
    icon: <DashboardIcon />,
  },
  {
    to: '/agencies',
    label: 'Agencies',
    permission: AGENCY_VIEW_PERMISSION,
    icon: <BuildingIcon />,
  },
  { to: '/audit', label: 'Audit log', permission: AUDIT_VIEW_PERMISSION, icon: <HistoryIcon /> },
  {
    to: '/users',
    label: 'Back-office users',
    permission: PLATFORM_USER_PERMISSION,
    icon: <PeopleIcon />,
  },
];

function NavItems() {
  const { session } = useAuth();
  if (!session) return null;

  const { claims } = session;

  return (
    <>
      {NAV.filter((item) => hasPermission(claims, item.permission)).map((item) => (
        <NavItem key={item.to} to={item.to} icon={item.icon}>
          {item.label}
        </NavItem>
      ))}

      {/* Its own entry rather than a row in the list above, because it carries a count and so
          has to fetch the queue — which only an account allowed to read it should do. */}
      {hasPermission(claims, KYB_REVIEW_PERMISSION) ? <KybNavItem /> : null}
    </>
  );
}

function NavItem({
  to,
  icon,
  children,
  badge,
}: {
  to: string;
  icon: ReactNode;
  children: ReactNode;
  badge?: ReactNode;
}) {
  return (
    <NavLink
      to={to}
      className={({ isActive }) =>
        cn(
          'flex items-center gap-2.5 rounded-md px-2.5 py-2 text-sm transition-colors',
          isActive
            ? 'bg-primary-subtle font-medium text-primary'
            : 'text-muted-foreground hover:bg-accent hover:text-accent-foreground',
        )
      }
    >
      {icon}
      <span className="flex-1 whitespace-nowrap">{children}</span>
      {badge}
    </NavLink>
  );
}

/** Its own component so the queue is only fetched for accounts allowed to read it. */
function KybNavItem() {
  const queue = useKybQueue();
  const waiting = queue.data?.length ?? 0;

  return (
    <NavItem
      to="/kyb"
      icon={<QueueIcon />}
      badge={
        waiting > 0 ? (
          <span className="rounded-full bg-primary px-1.5 text-xs font-medium tabular-nums text-primary-foreground">
            {waiting}
            <span className="sr-only"> waiting</span>
          </span>
        ) : null
      }
    >
      KYB review
    </NavItem>
  );
}
