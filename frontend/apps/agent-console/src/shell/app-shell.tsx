import { useEffect, useState } from 'react';
import { Menu } from 'lucide-react';
import { Outlet, useLocation } from 'react-router-dom';
import {
  Button,
  GlobalSearchInput,
  HelpIcon,
  NotificationBell,
  Sheet,
  SheetContent,
  SheetDescription,
  SheetTitle,
  cn,
} from '@trips/ui';
import { SidebarContent } from './sidebar';
import { UserMenu } from './user-menu';

/**
 * The frame around every signed-in screen.
 *
 *   ≥ 1024px  sidebar fixed on the left (collapsible), content beside it
 *   < 1024px  sidebar hidden; the menu button in the top bar opens it as a
 *             drawer over the page (tablet in portrait, phones)
 *
 * Screens render into `<Outlet />` inside `<main>`, and bring their own
 * `<PageHeader>`. They should not add their own outer padding or `<main>`.
 *
 * The header carries global chrome only (search, help, notifications,
 * account) — a page's own primary actions (e.g. Home's "Book travel") belong
 * in that page's own `<PageHeader actions>`, not here.
 */
export function AppShell() {
  const [navOpen, setNavOpen] = useState(false);
  const [collapsed, setCollapsed] = useState(false);
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

      <aside
        className={cn(
          'sticky top-0 hidden h-[calc(100vh-20px)] shrink-0 self-start rounded-[25px] border border-sidebar-border bg-sidebar transition-[width] duration-200 lg:block',
          'm-2.5',
          collapsed ? 'w-20' : 'w-[280px]',
        )}
      >
        <SidebarContent collapsed={collapsed} onToggleCollapse={() => setCollapsed((v) => !v)} />
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
        <header className="sticky top-0 z-30 flex h-[90px] items-center gap-4 bg-background px-4 sm:px-6 lg:px-8">
          <Button
            variant="ghost"
            size="icon"
            className="-ml-2 lg:hidden"
            aria-label="Open navigation"
            onClick={() => setNavOpen(true)}
          >
            <Menu aria-hidden="true" className="h-5 w-5" />
          </Button>

          <GlobalSearchInput className="hidden max-w-xl flex-1 sm:flex" />

          <div className="ml-auto flex items-center gap-3">
            <button
              type="button"
              aria-label="Help"
              className="flex size-10 items-center justify-center rounded-full bg-card hover:bg-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            >
              <HelpIcon size={20} />
            </button>
            <NotificationBell count={20} />
            <UserMenu />
          </div>
        </header>

        <main
          id="main"
          tabIndex={-1}
          className="mx-auto flex w-full max-w-6xl flex-1 flex-col gap-6 px-4 pb-6 outline-none sm:px-6 lg:px-8"
        >
          <Outlet />
        </main>
      </div>
    </div>
  );
}
