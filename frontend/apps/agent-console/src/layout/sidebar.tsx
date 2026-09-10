import { NavLink } from 'react-router-dom';
import { cn } from '@trips/ui';
import { NAV_ITEMS } from './nav-items';
import { CloseIcon } from './icons';

interface SidebarProps {
  /** Mobile/tablet drawer state. Ignored at desktop width, where it is always shown. */
  open: boolean;
  onClose: () => void;
}

function NavItems({ onNavigate }: { onNavigate: () => void }) {
  return (
    <nav aria-label="Main" className="flex flex-col gap-1 p-3">
      {NAV_ITEMS.map(({ label, to, icon: ItemIcon, comingSoon }) =>
        comingSoon ? (
          <span
            key={to}
            aria-disabled="true"
            title="Not built yet"
            className="flex cursor-not-allowed items-center gap-3 rounded-md px-3 py-2 text-sm text-muted-foreground opacity-60"
          >
            <ItemIcon />
            <span className="flex-1">{label}</span>
            <span className="text-xs uppercase tracking-wide">soon</span>
          </span>
        ) : (
          <NavLink
            key={to}
            to={to}
            end={to === '/'}
            onClick={onNavigate}
            className={({ isActive }) =>
              cn(
                'flex items-center gap-3 rounded-md px-3 py-2 text-sm font-medium transition-colors',
                'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2',
                isActive
                  ? 'bg-primary-subtle text-primary'
                  : 'text-muted-foreground hover:bg-accent hover:text-accent-foreground',
              )
            }
          >
            <ItemIcon />
            {label}
          </NavLink>
        ),
      )}
    </nav>
  );
}

/**
 * Sidebar navigation.
 *
 * Two renderings of one list, not two components: a permanent rail from `lg`
 * up, and a slide-over drawer below it. Issue #48 asks for "responsive down to
 * tablet width" — a travel agent on a 10" tablet at a counter is a real user
 * here, and a 256px rail on a 768px screen leaves no room for a fare table.
 */
export function Sidebar({ open, onClose }: SidebarProps) {
  return (
    <>
      {/* Desktop: always present, part of the page flow. */}
      <aside className="hidden w-64 shrink-0 border-r border-border bg-card lg:block">
        <div className="sticky top-0 flex h-screen flex-col">
          <div className="flex h-16 items-center border-b border-border px-5">
            <span className="text-base font-semibold tracking-tight text-foreground">
              Trips Agent
            </span>
          </div>
          <div className="flex-1 overflow-y-auto">
            <NavItems onNavigate={onClose} />
          </div>
        </div>
      </aside>

      {/* Tablet and below: a drawer. Rendered only when open so the links are
          not reachable by keyboard while it is hidden. */}
      {open ? (
        <div className="fixed inset-0 z-50 lg:hidden">
          <button
            type="button"
            aria-label="Close navigation"
            onClick={onClose}
            className="absolute inset-0 bg-foreground/40"
          />
          <aside
            role="dialog"
            aria-modal="true"
            aria-label="Main navigation"
            className="absolute inset-y-0 left-0 flex w-64 flex-col border-r border-border bg-card shadow-lg"
          >
            <div className="flex h-16 items-center justify-between border-b border-border px-5">
              <span className="text-base font-semibold tracking-tight text-foreground">
                Trips Agent
              </span>
              <button
                type="button"
                onClick={onClose}
                aria-label="Close navigation"
                className="rounded-md p-1 text-muted-foreground hover:bg-accent hover:text-accent-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
              >
                <CloseIcon />
              </button>
            </div>
            <div className="flex-1 overflow-y-auto">
              <NavItems onNavigate={onClose} />
            </div>
          </aside>
        </div>
      ) : null}
    </>
  );
}
