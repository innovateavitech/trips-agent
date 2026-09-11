import { useState } from 'react';
import { ChevronsUpDown } from 'lucide-react';
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuLabel,
  DropdownMenuRadioGroup,
  DropdownMenuRadioItem,
  DropdownMenuTrigger,
  cn,
} from '@trips/ui';
import { describeError } from '../api/errors';
import type { AgencyOption } from '../auth/auth-api';
import { useAgencies, useAuth, useCurrentUser } from '../auth/auth-provider';

const KIND_LABEL: Record<AgencyOption['kind'], string> = {
  principal: 'Principal agency',
  sub_agent: 'Sub-agent',
};

/**
 * Which agency the console is acting for.
 *
 * A principal agent manages sub-agents; switching re-scopes every screen to the
 * chosen agency. When there is only one agency — every account until the
 * sub-agent hierarchy ships (M3) — it is shown as a plain label, not a control
 * that opens onto a list of one.
 */
export function AgencySwitcher() {
  const user = useCurrentUser();
  const agencies = useAgencies();
  const { switchAgency } = useAuth();
  const [switching, setSwitching] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const current = user.agency;
  if (!current) return null;

  const options = agencies.data && agencies.data.length > 0 ? agencies.data : [current];
  const canSwitch = options.length > 1;

  async function choose(agencyId: string) {
    if (agencyId === current?.id) return;
    setSwitching(true);
    setError(null);
    try {
      await switchAgency(agencyId);
    } catch (caught) {
      setError(describeError(caught).title);
    } finally {
      setSwitching(false);
    }
  }

  const face = (
    <>
      <span
        aria-hidden="true"
        className="flex h-8 w-8 shrink-0 items-center justify-center rounded-md bg-sidebar-primary text-sm font-semibold text-sidebar"
      >
        {current.name.charAt(0).toUpperCase()}
      </span>
      <span className="flex min-w-0 flex-1 flex-col text-left">
        <span className="truncate text-sm font-medium text-sidebar-foreground">{current.name}</span>
        <span className="truncate text-xs text-sidebar-muted-foreground">
          {switching ? 'Switching…' : KIND_LABEL[current.kind]}
        </span>
      </span>
    </>
  );

  const surface = 'flex w-full items-center gap-3 rounded-lg bg-sidebar-accent px-2.5 py-2';

  if (!canSwitch) {
    return (
      <div className={surface} aria-label={`Acting for ${current.name}`}>
        {face}
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-1.5">
      <DropdownMenu>
        <DropdownMenuTrigger
          className={cn(
            surface,
            'transition-colors hover:bg-sidebar-border disabled:opacity-60',
            'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-sidebar-primary',
          )}
          aria-label={`Acting for ${current.name}. Switch agency`}
          disabled={switching}
        >
          {face}
          <ChevronsUpDown
            aria-hidden="true"
            className="h-4 w-4 shrink-0 text-sidebar-muted-foreground"
          />
        </DropdownMenuTrigger>
        <DropdownMenuContent align="start" className="w-64">
          <DropdownMenuLabel>Act for</DropdownMenuLabel>
          <DropdownMenuRadioGroup value={current.id} onValueChange={(id) => void choose(id)}>
            {options.map((agency) => (
              <DropdownMenuRadioItem key={agency.id} value={agency.id}>
                <span className="flex min-w-0 flex-col">
                  <span className="truncate">{agency.name}</span>
                  <span className="text-xs text-muted-foreground">{KIND_LABEL[agency.kind]}</span>
                </span>
              </DropdownMenuRadioItem>
            ))}
          </DropdownMenuRadioGroup>
        </DropdownMenuContent>
      </DropdownMenu>
      {error ? (
        <p role="alert" className="px-1 text-xs text-sidebar-foreground">
          {error}
        </p>
      ) : null}
    </div>
  );
}
