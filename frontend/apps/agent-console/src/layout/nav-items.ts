import type { ComponentType, SVGProps } from 'react';
import {
  BookingsIcon,
  CatalogIcon,
  DashboardIcon,
  SearchIcon,
  SettingsIcon,
  WalletIcon,
} from './icons';

export interface NavItem {
  label: string;
  to: string;
  icon: ComponentType<SVGProps<SVGSVGElement>>;
  /** The issue that builds the screen behind this link. Remove once it lands. */
  comingSoon?: boolean;
}

/**
 * The sidebar, in one list.
 *
 * Wallet is live (#86). The rest are still placeholders — the screens
 * arrive with #52 (search), #54 (bookings) and #55 (pricing).
 * They are listed now, disabled, so the shell's shape is reviewable and the
 * feature PRs only have to flip `comingSoon` off.
 */
export const NAV_ITEMS: NavItem[] = [
  { label: 'Dashboard', to: '/', icon: DashboardIcon },
  { label: 'Search', to: '/search', icon: SearchIcon, comingSoon: true },
  { label: 'Bookings', to: '/bookings', icon: BookingsIcon, comingSoon: true },
  { label: 'Wallet', to: '/wallet', icon: WalletIcon },
  { label: 'Catalog', to: '/catalog', icon: CatalogIcon, comingSoon: true },
  { label: 'Settings', to: '/settings', icon: SettingsIcon, comingSoon: true },
];
