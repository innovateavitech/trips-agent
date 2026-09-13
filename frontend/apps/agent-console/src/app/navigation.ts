import {
  Bus,
  CalendarDays,
  ClipboardList,
  Compass,
  Inbox,
  Landmark,
  LayoutDashboard,
  LifeBuoy,
  Network,
  Plane,
  ReceiptText,
  ShieldAlert,
  ShieldCheck,
  Globe,
  SlidersHorizontal,
  Ticket,
  Users,
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
  /**
   * Show this only to a principal.
   *
   * A sub-agent has no network of its own — the hierarchy is two levels — so
   * offering it a "Sub-agents" link would be a dead end. The API refuses it
   * either way, which is the guard that matters; this only keeps the sidebar
   * honest.
   */
  principalsOnly?: boolean;
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
      { label: 'Catalog', to: '/catalog', icon: Compass }, // build plan F3
      { label: 'Group departures', to: '/departures', icon: CalendarDays }, // build plan F6
    ],
  },
  {
    label: 'Customers',
    items: [
      { label: 'Leads', to: '/crm/leads', icon: Inbox }, // build plan F7
      { label: 'Customers', to: '/crm/customers', icon: Users }, // build plan F7
      { label: 'Tasks', to: '/crm/tasks', icon: ClipboardList }, // build plan F7
    ],
  },
  {
    label: 'Money',
    items: [
      { label: 'Wallet', to: '/wallet', icon: Wallet }, // #51
      { label: 'Pricing rules', to: '/pricing', icon: SlidersHorizontal }, // #55
      { label: 'Billing', to: '/billing', icon: ReceiptText }, // issues 64, 65
      { label: 'Payouts', to: '/payouts', icon: Landmark }, // build plan F12
      { label: 'Disputes', to: '/disputes', icon: ShieldAlert }, // build plan F12
    ],
  },
  {
    label: 'Network',
    items: [
      { label: 'Sub-agents', to: '/sub-agents', icon: Users, principalsOnly: true }, // issue 63
      { label: 'Network performance', to: '/network', icon: Network }, // issue 63
    ],
  },
  {
    label: 'Agency',
    items: [
      { label: 'Your website', to: '/website', icon: Globe }, // #58, #59
      { label: 'Business verification', to: '/verification', icon: ShieldCheck }, // #50
    ],
  },
];

export const NAV_ITEMS: NavItem[] = NAV_SECTIONS.flatMap((section) => section.items);

/**
 * The sidebar for one kind of agency, with empty sections dropped.
 *
 * A sub-agent still sees "Network performance": the same endpoint serves both,
 * and it answers with that agency's own figures alone.
 */
export function navigationFor(kind: 'principal' | 'sub_agent' | null): NavSection[] {
  if (kind !== 'sub_agent') {
    return NAV_SECTIONS;
  }

  return NAV_SECTIONS.map((section) => ({
    ...section,
    items: section.items.filter((item) => item.principalsOnly !== true),
  })).filter((section) => section.items.length > 0);
}
