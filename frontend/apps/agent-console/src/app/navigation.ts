import {
  BarChart3,
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
  FileSpreadsheet,
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
  /**
   * Show this only to an agency Owner.
   *
   * Business verification is the agency's legal identity, and since issue 172
   * every `/api/v1/kyb` route needs `kyb.submit` — which, of the agency roles,
   * only the Owner holds. The API refuses the rest either way; this keeps the
   * sidebar from offering a screen that can only answer "no".
   */
  ownersOnly?: boolean;
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
    label: 'Insight',
    items: [
      { label: 'Analytics', to: '/analytics', icon: BarChart3 }, // issue 67
      { label: 'Reports', to: '/reports', icon: FileSpreadsheet }, // issue 68
    ],
  },
  {
    label: 'Agency',
    items: [
      { label: 'Your website', to: '/website', icon: Globe }, // #58, #59
      { label: 'Business verification', to: '/verification', icon: ShieldCheck, ownersOnly: true }, // #50
    ],
  },
];

export const NAV_ITEMS: NavItem[] = NAV_SECTIONS.flatMap((section) => section.items);

/** The name the token carries for an agency's owner. */
const OWNER_ROLE = 'Owner';

/**
 * Whether these roles may manage the agency's KYB submission.
 *
 * Roles arrive from the token by name. When the session carries permissions
 * rather than roles, this becomes `permissions.includes('kyb.submit')`.
 */
export function canManageVerification(roles: readonly string[]): boolean {
  return roles.includes(OWNER_ROLE);
}

/**
 * The sidebar for one kind of agency and one set of roles, with empty sections
 * dropped.
 *
 * A sub-agent still sees "Network performance": the same endpoint serves both,
 * and it answers with that agency's own figures alone. Business verification is
 * the Owner's, because every KYB route now asks for `kyb.submit` (issue 172).
 */
export function navigationFor(
  kind: 'principal' | 'sub_agent' | null,
  roles: readonly string[] = [],
): NavSection[] {
  const owner = canManageVerification(roles);

  return NAV_SECTIONS.map((section) => ({
    ...section,
    items: section.items.filter(
      (item) =>
        (item.principalsOnly !== true || kind !== 'sub_agent') &&
        (item.ownersOnly !== true || owner),
    ),
  })).filter((section) => section.items.length > 0);
}
