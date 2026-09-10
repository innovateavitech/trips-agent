import { useCallback, useRef, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { cn } from '@trips/ui';
import { useAuth } from '../auth/auth-context';
import { useDismissable } from '../hooks/use-dismissable';
import { CheckIcon, ChevronDownIcon } from './icons';

/**
 * Switches which agency the console is acting as.
 *
 * Only a principal agency manages sub-agencies, so for most agents this is a
 * single name and no dropdown at all — rendering a menu with one item invites
 * the question "what else is in here?".
 */
export function AgencySwitcher() {
  const { session, switchAgency } = useAuth();
  const [open, setOpen] = useState(false);
  const containerRef = useRef<HTMLDivElement>(null);
  const queryClient = useQueryClient();

  const close = useCallback(() => setOpen(false), []);
  useDismissable(containerRef, open, close);

  if (!session) return null;

  const { activeAgency, agencies } = session;

  if (agencies.length <= 1) {
    return (
      <span className="truncate text-sm font-medium text-foreground" title={activeAgency.name}>
        {activeAgency.name}
      </span>
    );
  }

  function handleSelect(agencyId: string) {
    switchAgency(agencyId);
    /**
     * Every cached query was fetched for the PREVIOUS agency. Keeping it would
     * show one agency's bookings and prices under another's name — the exact
     * cross-tenant leak CLAUDE.md rule 3 guards on the server. Clear, do not
     * invalidate: invalidate refetches and briefly renders stale data.
     */
    queryClient.clear();
    setOpen(false);
  }

  return (
    <div ref={containerRef} className="relative">
      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        aria-haspopup="listbox"
        aria-expanded={open}
        className="flex max-w-xs items-center gap-2 rounded-md border border-border px-3 py-1.5 text-sm font-medium text-foreground hover:bg-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
      >
        <span className="truncate">{activeAgency.name}</span>
        <ChevronDownIcon className="h-4 w-4 shrink-0 text-muted-foreground" />
      </button>

      {open ? (
        <ul
          role="listbox"
          aria-label="Switch agency"
          className="absolute left-0 z-50 mt-1 max-h-72 w-64 overflow-y-auto rounded-md border border-border bg-popover p-1 shadow-lg"
        >
          {agencies.map((agency) => {
            const isActive = agency.id === activeAgency.id;
            return (
              <li key={agency.id}>
                <button
                  type="button"
                  role="option"
                  aria-selected={isActive}
                  onClick={() => handleSelect(agency.id)}
                  className={cn(
                    'flex w-full items-center gap-2 rounded-sm px-2 py-2 text-left text-sm',
                    'hover:bg-accent hover:text-accent-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring',
                    isActive ? 'text-primary' : 'text-popover-foreground',
                  )}
                >
                  <span className="flex-1 truncate">{agency.name}</span>
                  {agency.kybStatus !== 'verified' ? (
                    <span className="shrink-0 text-xs text-warning">{agency.kybStatus}</span>
                  ) : null}
                  {isActive ? <CheckIcon className="h-4 w-4 shrink-0" /> : null}
                </button>
              </li>
            );
          })}
        </ul>
      ) : null}
    </div>
  );
}
