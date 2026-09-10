import { AgencySwitcher } from './agency-switcher';
import { MenuIcon } from './icons';
import { UserMenu } from './user-menu';

interface TopBarProps {
  onOpenNav: () => void;
}

export function TopBar({ onOpenNav }: TopBarProps) {
  return (
    <header className="sticky top-0 z-40 flex h-16 shrink-0 items-center gap-3 border-b border-border bg-background px-4 sm:px-6">
      {/* The only way to reach navigation below `lg`, so it must never be hidden
          behind a hover state — a tablet has no hover. */}
      <button
        type="button"
        onClick={onOpenNav}
        aria-label="Open navigation"
        className="rounded-md p-2 text-muted-foreground hover:bg-accent hover:text-accent-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring lg:hidden"
      >
        <MenuIcon />
      </button>

      <AgencySwitcher />

      <div className="ml-auto flex items-center gap-2">
        <UserMenu />
      </div>
    </header>
  );
}
