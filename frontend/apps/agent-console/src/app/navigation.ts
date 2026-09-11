import {
  Bus,
  LayoutDashboard,
  LifeBuoy,
  Plane,
  ShieldCheck,
  SlidersHorizontal,
  Ticket,
  Wallet,
  type LucideIcon,
} from 'lucide-react';

/**
 * ============================================================================
 *  The sidebar, as data.
 * ============================================================================
 *
 * Adding a screen is two edits, and neither is in the shell:
 *   1. a route in your feature's `routes.tsx` (already stubbed for M1 screens);
 *   2. an entry here, if it belongs in the sidebar.
 *
 * `navigation.test.ts` fails if an entry points at a path no route serves, so
 * the sidebar can never offer a dead link.
 */

export interface NavItem {
  label: string;
  to: string;
  icon: LucideIcon;
  /**
   * Highlight only on this exact path. The dashboard needs it — every path
   * starts with `/` — and so does anything with a sibling nested under it.
   */
  end?: boolean;
}

export interface NavSection {
  /** Sentence case, shown quietly above the group. */
  label: string;
  items: NavItem[];
}

export const NAV_SECTIONS: NavSection[] = [
  {
    label: 'Sell',
    items: [
      { label: 'Dashboard', to: '/', icon: LayoutDashboard, end: true },
      { label: 'Flights', to: '/search/flights', icon: Plane }, // #52
      { label: 'Buses', to: '/search/buses', icon: Bus }, // #52
      { label: 'Bookings', to: '/bookings', icon: Ticket }, // #54
      { label: 'Resolution queue', to: '/resolution', icon: LifeBuoy }, // #54
    ],
  },
  {
    label: 'Money',
    items: [
      { label: 'Wallet', to: '/wallet', icon: Wallet }, // #51
      { label: 'Pricing rules', to: '/pricing', icon: SlidersHorizontal }, // #55
    ],
  },
  {
    label: 'Agency',
    items: [
      { label: 'Business verification', to: '/verification', icon: ShieldCheck }, // #50
    ],
  },
];

export const NAV_ITEMS: NavItem[] = NAV_SECTIONS.flatMap((section) => section.items);
