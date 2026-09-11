import { useEffect, useState } from 'react';
import { Menu, Plus } from 'lucide-react';
import { Link, Outlet, useLocation } from 'react-router-dom';
import {
  Button,
  Sheet,
  SheetContent,
  SheetDescription,
  SheetTitle,
  buttonVariants,
} from '@trips/ui';
import { SidebarContent } from './sidebar';
import { UserMenu } from './user-menu';

/**
 * The frame around every signed-in screen.
 *
 *   ≥ 1024px  sidebar fixed on the left, content beside it
 *   < 1024px  sidebar hidden; the menu button in the top bar opens it as a
 *             drawer over the page (tablet in portrait, phones)
 *
 * Screens render into `<Outlet />` inside `<main>`, and bring their own
 * `<PageHeader>`. They should not add their own outer padding or `<main>`.
 */
export function AppShell() {
  const [navOpen, setNavOpen] = useState(false);
  const location = useLocation();

  // Close the drawer whenever the route changes — including Back/Forward,
  // which never pass through a link's onClick.
  useEffect(() => {
    setNavOpen(false);
  }, [location.pathname]);

  return (
    <div className="flex min-h-screen bg-background">
      <a
        href="#main"
        className="sr-only focus:not-sr-only focus:fixed focus:left-4 focus:top-4 focus:z-50 focus:rounded-md focus:bg-background focus:px-4 focus:py-2 focus:text-sm focus:shadow-lg"
      >
        Skip to content
      </a>

      <aside className="sticky top-0 hidden h-screen w-64 shrink-0 border-r border-sidebar-border bg-sidebar lg:block">
        <SidebarContent />
      </aside>

      <Sheet open={navOpen} onOpenChange={setNavOpen}>
        <SheetContent className="bg-sidebar lg:hidden">
          <SheetTitle className="sr-only">Navigation</SheetTitle>
          <SheetDescription className="sr-only">
            Move between the sections of the agent console.
          </SheetDescription>
          <SidebarContent onNavigate={() => setNavOpen(false)} />
        </SheetContent>
      </Sheet>

      <div className="flex min-w-0 flex-1 flex-col">
        <header className="sticky top-0 z-30 flex h-14 items-center gap-3 border-b border-border bg-background px-4 sm:px-6 lg:px-8">
          <Button
            variant="ghost"
            size="icon"
            className="-ml-2 lg:hidden"
            aria-label="Open navigation"
            onClick={() => setNavOpen(true)}
          >
            <Menu aria-hidden="true" className="h-5 w-5" />
          </Button>

          <div className="ml-auto flex items-center gap-2 sm:gap-3">
            <Link to="/search/flights" className={buttonVariants({ size: 'sm' })}>
              <Plus aria-hidden="true" className="h-4 w-4" />
              New booking
            </Link>
            <UserMenu />
          </div>
        </header>

        <main
          id="main"
          tabIndex={-1}
          className="mx-auto flex w-full max-w-6xl flex-1 flex-col gap-6 px-4 py-6 outline-none sm:px-6 lg:px-8 lg:py-8"
        >
          <Outlet />
        </main>
      </div>
    </div>
  );
}
