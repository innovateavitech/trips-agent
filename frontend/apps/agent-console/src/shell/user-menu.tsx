import { ChevronDown, LogOut, ShieldCheck } from 'lucide-react';
import { useNavigate } from 'react-router-dom';
import {
  Badge,
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@trips/ui';
import { displayNameFor, initialsFor, roleLabel } from '../auth/auth-api';
import { useAuth, useCurrentUser } from '../auth/auth-provider';
import { SIGN_IN_PATH } from '../auth/redirect';

export function UserMenu() {
  const user = useCurrentUser();
  const { signOut } = useAuth();
  const navigate = useNavigate();
  const name = displayNameFor(user);

  async function handleSignOut() {
    await signOut();
    navigate(SIGN_IN_PATH, { replace: true });
  }

  return (
    <DropdownMenu>
      <DropdownMenuTrigger
        className="flex items-center gap-2 rounded-full py-1 pl-1 pr-2 transition-colors hover:bg-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
        aria-label={`Account menu for ${name}`}
      >
        <span
          aria-hidden="true"
          className="flex h-8 w-8 items-center justify-center rounded-full bg-primary-subtle text-xs font-semibold text-primary"
        >
          {initialsFor(user)}
        </span>
        <span className="hidden max-w-40 truncate text-sm font-medium text-foreground md:inline">
          {name}
        </span>
        <ChevronDown aria-hidden="true" className="h-4 w-4 text-muted-foreground" />
      </DropdownMenuTrigger>

      <DropdownMenuContent align="end" className="w-64">
        <div className="flex flex-col gap-1.5 px-2 py-2">
          <p className="truncate text-sm font-medium text-foreground">{name}</p>
          <p className="truncate text-xs text-muted-foreground">{user.email}</p>
          {user.roles.length > 0 ? (
            <div className="flex flex-wrap gap-1 pt-0.5">
              {user.roles.map((role) => (
                <Badge key={role} tone="primary">
                  {roleLabel(role)}
                </Badge>
              ))}
            </div>
          ) : null}
        </div>
        <DropdownMenuSeparator />
        <DropdownMenuLabel>Your agency</DropdownMenuLabel>
        <DropdownMenuItem onSelect={() => navigate('/verification')}>
          <ShieldCheck aria-hidden="true" className="h-4 w-4 text-muted-foreground" />
          Business verification
        </DropdownMenuItem>
        <DropdownMenuSeparator />
        <DropdownMenuItem onSelect={() => void handleSignOut()}>
          <LogOut aria-hidden="true" className="h-4 w-4 text-muted-foreground" />
          Sign out
        </DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
