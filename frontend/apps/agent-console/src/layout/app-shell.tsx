import { useCallback, useEffect, useState } from 'react';
import { Outlet, useLocation } from 'react-router-dom';
import { Sidebar } from './sidebar';
import { TopBar } from './top-bar';

/**
 * The frame every authenticated screen renders inside.
 *
 * Feature screens render into the `<Outlet>` and should NOT add their own
 * padding wrapper — the `max-w-7xl` container here is what keeps a fare table
 * on a 27" monitor from stretching to an unreadable line length.
 */
export function AppShell() {
  const [navOpen, setNavOpen] = useState(false);
  const location = useLocation();

  const closeNav = useCallback(() => setNavOpen(false), []);

  /* Close the drawer on navigation. Without this, tapping a link on a tablet
     leaves the drawer covering the screen you just asked for. */
  useEffect(() => {
    setNavOpen(false);
  }, [location.pathname]);

  return (
    <div className="flex min-h-screen bg-background">
      <Sidebar open={navOpen} onClose={closeNav} />

      <div className="flex min-w-0 flex-1 flex-col">
        <TopBar onOpenNav={() => setNavOpen(true)} />

        <main className="flex-1 px-4 py-6 sm:px-6 lg:px-8">
          <div className="mx-auto w-full max-w-7xl">
            <Outlet />
          </div>
        </main>
      </div>
    </div>
  );
}
