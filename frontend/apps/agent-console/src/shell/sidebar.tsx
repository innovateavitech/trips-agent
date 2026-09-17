import { NavLink } from 'react-router-dom';
import { cn, HelpIcon, SidebarCollapseIcon } from '@trips/ui';
import { navigationFor } from '../app/navigation';
import { useCurrentUser } from '../auth/auth-provider';
import { AgencySwitcher } from './agency-switcher';
import outsydeLogo from '../assets/outsyde-logo.png';

/**
 * The navigation rail. The same content renders in two places: fixed on the
 * left from `lg` up, and inside the slide-in drawer below that (tablet and
 * phone). `onNavigate` lets the drawer close itself when a link is chosen.
 *
 * `collapsed`/`onToggleCollapse` only apply to the fixed `lg` rail — the
 * drawer is always shown expanded, since collapsing a temporary overlay
 * buys nothing.
 */
export function SidebarContent({
  onNavigate,
  collapsed = false,
  onToggleCollapse,
}: {
  onNavigate?: () => void;
  collapsed?: boolean;
  onToggleCollapse?: () => void;
}) {
  // A sub-agent has no network of its own, so it is not offered one.
  const sections = navigationFor(useCurrentUser().agency?.kind ?? null);

  return (
    <div className="flex h-full flex-col gap-6 p-3">
      <div className="flex items-center gap-2 px-1">
        {!collapsed ? (
          <img src={outsydeLogo} alt="Outsyde" className="h-9 w-auto shrink-0" />
        ) : (
          <span
            aria-hidden="true"
            className="flex h-8 w-8 shrink-0 items-center justify-center rounded-lg bg-primary text-sm font-bold text-primary-foreground"
          >
            O
          </span>
        )}
        {onToggleCollapse ? (
          <button
            type="button"
            onClick={onToggleCollapse}
            aria-label={collapsed ? 'Expand navigation' : 'Collapse navigation'}
            aria-pressed={collapsed}
            className="ml-auto flex h-7 w-7 shrink-0 items-center justify-center rounded-md text-sidebar-muted-foreground hover:bg-sidebar-accent hover:text-sidebar-accent-foreground"
          >
            <SidebarCollapseIcon
              className={cn('size-4 transition-transform', collapsed && 'rotate-180')}
            />
          </button>
        ) : null}
      </div>

      {!collapsed ? <AgencySwitcher /> : null}

      <nav aria-label="Main" className="flex flex-1 flex-col overflow-y-auto">
        {sections.map((section, index) => (
          <div
            key={section.label ?? section.items[0]?.to ?? index}
            className={cn(
              'flex flex-col gap-0.5 py-3',
              index > 0 && 'border-t border-sidebar-border',
            )}
          >
            {!collapsed && section.label ? (
              <p className="px-3 pb-1 text-xs font-medium text-sidebar-muted-foreground">
                {section.label}
              </p>
            ) : null}
            {section.items.map((item) => (
              <NavLink
                key={item.to}
                to={item.to}
                end={item.end}
                onClick={onNavigate}
                title={collapsed ? item.label : undefined}
                className={({ isActive }) =>
                  cn(
                    'group flex items-center gap-3 rounded-lg px-3 py-2 text-sm transition-colors',
                    collapsed && 'justify-center px-2',
                    isActive
                      ? 'bg-sidebar-accent font-medium text-sidebar-accent-foreground'
                      : 'text-sidebar-muted-foreground hover:bg-sidebar-accent hover:text-sidebar-accent-foreground',
                  )
                }
              >
                {({ isActive }) => (
                  <>
                    <item.icon
                      className={cn(
                        'h-4 w-4 shrink-0',
                        isActive
                          ? 'text-sidebar-accent-foreground'
                          : 'text-sidebar-muted-foreground',
                      )}
                    />
                    {!collapsed ? item.label : null}
                  </>
                )}
              </NavLink>
            ))}
          </div>
        ))}
      </nav>

      <a
        href="mailto:support@outsyde.app"
        title={collapsed ? 'Help' : undefined}
        className={cn(
          'flex items-center gap-3 rounded-lg px-3 py-2 text-sm text-sidebar-muted-foreground transition-colors hover:bg-sidebar-accent hover:text-sidebar-accent-foreground',
          collapsed && 'justify-center px-2',
        )}
      >
        <HelpIcon className="h-4 w-4 shrink-0" />
        {!collapsed ? 'Help' : null}
      </a>
    </div>
  );
}
