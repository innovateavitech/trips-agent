import type { FC } from 'react';
import {
  Add,
  Airplane,
  ArrowDown,
  ArrowDown2,
  Bag2,
  Bill,
  Bus,
  Chart2,
  DocumentText,
  Export,
  Global,
  Home,
  MessageQuestion,
  NotificationBing,
  Profile2User,
  SearchNormal1,
  SidebarLeft,
  Setting2,
  Tag,
  TrendUp,
  Wallet2,
  type Icon,
  type IconProps,
} from 'iconsax-reactjs';

/**
 * Icons for the redesigned surfaces only (the Home page and shell) — sourced
 * from Iconsax to match the Figma design exactly. Everywhere else in the app
 * keeps using `lucide-react`; this is not a wholesale icon migration.
 *
 * Each export is pre-set to the "Outline" variant (the only one the design
 * uses) and given an application-meaningful name, since Iconsax's own names
 * (`Bag2`, `Wallet2`, `Setting2` — numbered where a family has more than one
 * take on a glyph) read as implementation detail at a call site.
 */
function outline(IconComponent: Icon): FC<Omit<IconProps, 'variant'>> {
  return function OutlineIcon(props) {
    return <IconComponent variant="Outline" {...props} />;
  };
}

export const HomeIcon = outline(Home);
export const TravelIcon = outline(Bag2);
export const CustomersIcon = outline(Profile2User);
export const AnalyticsIcon = outline(Chart2);
export const WalletIcon = outline(Wallet2);
export const InvoicesIcon = outline(Bill);
export const ReportsIcon = outline(DocumentText);
export const OnlineStoreIcon = outline(Global);
export const CatalogueIcon = outline(Tag);
export const HelpIcon = outline(MessageQuestion);
export const SearchIcon = outline(SearchNormal1);
export const NotificationIcon = outline(NotificationBing);
export const AddIcon = outline(Add);
export const CircularArrowDownIcon = outline(ArrowDown);
export const ChevronDownIcon = outline(ArrowDown2);
export const SidebarCollapseIcon = outline(SidebarLeft);
export const ConfigurationIcon = outline(Setting2);
export const BookTravelIcon = outline(Export);
export const BusIcon = outline(Bus);
export const AirplaneIcon = outline(Airplane);
export const TrendUpIcon = outline(TrendUp);
