import { NavLink } from 'react-router-dom';
import { cn } from '@trips/ui';
import { NAV_SECTIONS } from '../app/navigation';
import { AgencySwitcher } from './agency-switcher';

/**
 * The navigation rail. The same content renders in two places: fixed on the
 * left from `lg` up, and inside the slide-in drawer below that (tablet and
 * phone). `onNavigate` lets the drawer close itself when a link is chosen.
 */
export function SidebarContent({ onNavigate }: { onNavigate?: () => void }) {
  return (
    <div className="flex h-full flex-col gap-6 px-3 py-4">
      <div className="flex items-center gap-2.5 px-2">
        <span
          aria-hidden="true"
          className="flex h-7 w-7 items-center justify-center rounded-md bg-primary text-sm font-bold text-primary-foreground"
        >
          T
        </span>
        <span className="flex flex-col leading-tight">
          <span className="text-sm font-semibold text-sidebar-foreground">Trips</span>
          <span className="text-xs text-sidebar-muted-foreground">Agent console</span>
        </span>
      </div>

      <AgencySwitcher />

      <nav aria-label="Main" className="flex flex-1 flex-col gap-5 overflow-y-auto">
        {NAV_SECTIONS.map((section) => (
          <div key={section.label} className="flex flex-col gap-0.5">
            <p className="px-3 pb-1 text-xs font-medium text-sidebar-muted-foreground">
              {section.label}
            </p>
            {section.items.map((item) => (
              <NavLink
                key={item.to}
                to={item.to}
                end={item.end}
                onClick={onNavigate}
                className={({ isActive }) =>
                  cn(
                    'group flex items-center gap-3 rounded-md px-3 py-2 text-sm transition-colors',
                    isActive
                      ? 'bg-sidebar-accent font-medium text-sidebar-accent-foreground'
                      : 'text-sidebar-muted-foreground hover:bg-sidebar-accent hover:text-sidebar-accent-foreground',
                  )
                }
              >
                {({ isActive }) => (
                  <>
                    <item.icon
                      aria-hidden="true"
                      className={cn(
                        'h-4 w-4 shrink-0',
                        isActive ? 'text-sidebar-primary' : 'text-sidebar-muted-foreground',
                      )}
                    />
                    {item.label}
                  </>
                )}
              </NavLink>
            ))}
          </div>
        ))}
      </nav>
    </div>
  );
}
