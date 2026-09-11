import { useEffect, useId, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { cn } from '@trips/ui';
import { ChevronDownIcon, SignOutIcon } from '../components/icons';
import { initialsFor } from '../lib/auth/claims';
import { useAuth } from '../features/auth/auth-context';

/**
 * Who is signed in, and the way out.
 *
 * The role is shown next to the email on purpose: on a shared operations desk "am I signed in as
 * me, or as the Super Admin account someone left open?" is a real question, and the answer
 * decides what the next click is allowed to do.
 */
export function UserMenu() {
  const { session, signOut } = useAuth();
  const navigate = useNavigate();
  const [open, setOpen] = useState(false);
  const [signingOut, setSigningOut] = useState(false);
  const containerRef = useRef<HTMLDivElement>(null);
  const buttonRef = useRef<HTMLButtonElement>(null);
  const menuId = useId();

  useEffect(() => {
    if (!open) return;

    const closeOnOutsideClick = (event: PointerEvent) => {
      if (!containerRef.current?.contains(event.target as Node)) setOpen(false);
    };
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        setOpen(false);
        buttonRef.current?.focus();
      }
    };

    document.addEventListener('pointerdown', closeOnOutsideClick);
    document.addEventListener('keydown', closeOnEscape);
    return () => {
      document.removeEventListener('pointerdown', closeOnOutsideClick);
      document.removeEventListener('keydown', closeOnEscape);
    };
  }, [open]);

  if (!session) return null;

  const { email, roles } = session.claims;
  const role = roles.join(', ') || 'Trips staff';

  async function handleSignOut() {
    setSigningOut(true);
    await signOut();
    navigate('/sign-in', { replace: true, state: { signedOut: true } });
  }

  return (
    <div ref={containerRef} className="relative">
      <button
        ref={buttonRef}
        type="button"
        aria-haspopup="menu"
        aria-expanded={open}
        aria-controls={menuId}
        onClick={() => setOpen((value) => !value)}
        className="flex items-center gap-2.5 rounded-md px-2 py-1.5 text-left transition-colors hover:bg-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
      >
        <span
          aria-hidden="true"
          className="flex h-8 w-8 items-center justify-center rounded-full bg-primary-subtle text-xs font-semibold text-primary"
        >
          {initialsFor(email)}
        </span>
        <span className="hidden flex-col leading-tight sm:flex">
          <span className="text-sm font-medium text-foreground">{email}</span>
          <span className="text-xs text-muted-foreground">{role}</span>
        </span>
        <span className="sr-only sm:hidden">Account menu for {email}</span>
        <ChevronDownIcon
          className={cn('h-4 w-4 text-muted-foreground transition-transform', open && 'rotate-180')}
        />
      </button>

      {open ? (
        <div
          id={menuId}
          role="menu"
          aria-label="Account"
          className="absolute right-0 top-full z-20 mt-2 w-64 animate-slide-up rounded-lg border border-border bg-popover p-1 text-popover-foreground shadow-md"
        >
          <div className="border-b border-border px-3 py-2.5">
            <p className="truncate text-sm font-medium text-foreground">{email}</p>
            <p className="text-xs text-muted-foreground">{role}</p>
          </div>
          <button
            type="button"
            role="menuitem"
            onClick={() => void handleSignOut()}
            disabled={signingOut}
            className="mt-1 flex w-full items-center gap-2 rounded-md px-3 py-2 text-sm text-foreground transition-colors hover:bg-accent focus-visible:bg-accent focus-visible:outline-none disabled:opacity-50"
          >
            <SignOutIcon />
            {signingOut ? 'Signing out' : 'Sign out'}
          </button>
        </div>
      ) : null}
    </div>
  );
}
