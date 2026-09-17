import {
  AnalyticsIcon,
  CatalogueIcon,
  CustomersIcon,
  HomeIcon,
  InvoicesIcon,
  OnlineStoreIcon,
  TravelIcon,
  WalletIcon,
} from '@trips/ui';
import type { ComponentType } from 'react';

/**
 * ============================================================================
 *  The sidebar, as data.
 * ============================================================================
 *
 * This is a literal match to the Figma Home frame's sidebar: eight flat rows,
 * three groups separated by a divider, no group-label text, no expansion.
 * Each top-level item is one row linking to one route — it is NOT a section
 * header over a list of sub-pages. That was this file's first draft, and it
 * put rows on screen the design never had; don't reintroduce it.
 *
 * Everything the flat nav doesn't surface (flight/bus search, the bookings
 * list, the resolution queue, group departures, leads/tasks, reports, and
 * the six sections already commented out below) still has a real route —
 * see each feature's own `routes.tsx` — it's just not linked from here yet.
 * Reachable by direct URL, and from Home's own cards/links where relevant.
 */

export interface NavItem {
  label: string;
  to: string;
  icon: ComponentType<{ className?: string }>;
  /**
   * Highlight only on this exact path. Home needs it — every path starts
   * with `/` — and so does anything with a sibling nested under it.
   */
  end?: boolean;
  /** Show this only to a principal. Two-level hierarchy, so a sub-agent has no network of its own. */
  principalsOnly?: boolean;
}

export interface NavSection {
  /** Sentence case, shown quietly above the group — omit it, Figma has none. */
  label?: string;
  items: NavItem[];
}

export const NAV_SECTIONS: NavSection[] = [
  {
    items: [
      { label: 'Home', to: '/', icon: HomeIcon, end: true },
      { label: 'Travel', to: '/bookings', icon: TravelIcon },
      { label: 'Customers', to: '/crm/customers', icon: CustomersIcon },
      { label: 'Analytics', to: '/analytics', icon: AnalyticsIcon },
    ],
  },
  {
    items: [
      { label: 'Wallet', to: '/wallet', icon: WalletIcon },
      { label: 'Invoices', to: '/invoices', icon: InvoicesIcon },
    ],
  },
  {
    items: [
      { label: 'Online store', to: '/website', icon: OnlineStoreIcon },
      { label: 'Catalogue', to: '/catalog', icon: CatalogueIcon },
    ],
  },

  // ---------------------------------------------------------------------
  // Commented out for this pass — no Figma frame yet. Not deleted: the
  // routes and features below are untouched and still reachable directly.
  // ---------------------------------------------------------------------
  // {
  //   label: 'Money',
  //   items: [
  //     { label: 'Pricing rules', to: '/pricing', icon: SlidersHorizontal }, // #55
  //     { label: 'Billing', to: '/billing', icon: ReceiptText }, // issues 64, 65
  //     { label: 'Payouts', to: '/payouts', icon: Landmark }, // build plan F12
  //     { label: 'Disputes', to: '/disputes', icon: ShieldAlert }, // build plan F12
  //   ],
  // },
  // {
  //   label: 'Network',
  //   items: [
  //     { label: 'Sub-agents', to: '/sub-agents', icon: Users, principalsOnly: true }, // issue 63
  //     { label: 'Network performance', to: '/network', icon: Network }, // issue 63
  //   ],
  // },
  // {
  //   label: 'Agency',
  //   items: [
  //     { label: 'Business verification', to: '/verification', icon: ShieldCheck }, // #50
  //   ],
  // },
];

export const NAV_ITEMS: NavItem[] = NAV_SECTIONS.flatMap((section) => section.items);

/**
 * The sidebar for one kind of agency, with empty sections dropped.
 *
 * A sub-agent still sees "Network performance" once that section returns:
 * the same endpoint serves both, and it answers with that agency's own
 * figures alone.
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
