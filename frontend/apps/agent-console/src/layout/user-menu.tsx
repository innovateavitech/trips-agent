import { useCallback, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useAuth } from '../auth/auth-context';
import { useDismissable } from '../hooks/use-dismissable';
import { ChevronDownIcon } from './icons';

/** "Ada Nwosu" → "AN". Falls back to the first letter of the email. */
function initialsOf(fullName: string, email: string): string {
  const parts = fullName.trim().split(/\s+/).filter(Boolean);
  if (parts.length === 0) return (email[0] ?? '?').toUpperCase();
  const first = parts[0]?.[0] ?? '';
  const last = parts.length > 1 ? (parts[parts.length - 1]?.[0] ?? '') : '';
  return (first + last).toUpperCase();
}

export function UserMenu() {
  const { session, signOut } = useAuth();
  const [open, setOpen] = useState(false);
  const [signingOut, setSigningOut] = useState(false);
  const containerRef = useRef<HTMLDivElement>(null);
  const navigate = useNavigate();

  const close = useCallback(() => setOpen(false), []);
  useDismissable(containerRef, open, close);

  if (!session) return null;

  const { user } = session;

  async function handleSignOut() {
    setSigningOut(true);
    try {
      await signOut();
      navigate('/login', { replace: true });
    } finally {
      setSigningOut(false);
    }
  }

  return (
    <div ref={containerRef} className="relative">
      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        aria-haspopup="menu"
        aria-expanded={open}
        className="flex items-center gap-2 rounded-md p-1 hover:bg-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
      >
        <span
          aria-hidden="true"
          className="flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-primary-subtle text-xs font-semibold text-primary"
        >
          {initialsOf(user.fullName, user.email)}
        </span>
        <span className="sr-only">Account menu for {user.fullName}</span>
        <ChevronDownIcon className="h-4 w-4 text-muted-foreground" />
      </button>

      {open ? (
        <div
          role="menu"
          aria-label="Account"
          className="absolute right-0 z-50 mt-1 w-56 rounded-md border border-border bg-popover p-1 shadow-lg"
        >
          <div className="border-b border-border px-3 py-2">
            <p className="truncate text-sm font-medium text-popover-foreground">{user.fullName}</p>
            <p className="truncate text-xs text-muted-foreground">{user.email}</p>
          </div>

          <button
            type="button"
            role="menuitem"
            disabled={signingOut}
            onClick={() => void handleSignOut()}
            className="mt-1 w-full rounded-sm px-3 py-2 text-left text-sm text-popover-foreground hover:bg-accent hover:text-accent-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-50"
          >
            {signingOut ? 'Signing out…' : 'Sign out'}
          </button>
        </div>
      ) : null}
    </div>
  );
}
