import type { SVGProps } from 'react';

/**
 * A deliberately tiny icon set, drawn inline.
 *
 * We do not pull in an icon package for eight glyphs — that is a dependency,
 * a bundle cost and a licence to audit for something a few paths cover. If the
 * count grows past ~20, revisit and add `lucide-react` properly.
 *
 * Every icon inherits `currentColor`, so colour comes from the parent's text
 * token and never from the icon itself.
 */

type IconProps = SVGProps<SVGSVGElement>;

function Icon({ children, ...props }: IconProps) {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={1.75}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      focusable="false"
      className="h-5 w-5"
      {...props}
    >
      {children}
    </svg>
  );
}

export const DashboardIcon = (props: IconProps) => (
  <Icon {...props}>
    <rect x="3" y="3" width="7" height="9" rx="1" />
    <rect x="14" y="3" width="7" height="5" rx="1" />
    <rect x="14" y="12" width="7" height="9" rx="1" />
    <rect x="3" y="16" width="7" height="5" rx="1" />
  </Icon>
);

export const SearchIcon = (props: IconProps) => (
  <Icon {...props}>
    <circle cx="11" cy="11" r="7" />
    <path d="m20 20-3.5-3.5" />
  </Icon>
);

export const BookingsIcon = (props: IconProps) => (
  <Icon {...props}>
    <path d="M4 5a1 1 0 0 1 1-1h14a1 1 0 0 1 1 1v15l-4-2-4 2-4-2-4 2z" />
    <path d="M8 9h8M8 13h5" />
  </Icon>
);

export const WalletIcon = (props: IconProps) => (
  <Icon {...props}>
    <path d="M3 7a2 2 0 0 1 2-2h12a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z" />
    <path d="M16 12h5v-3h-5a1.5 1.5 0 0 0 0 3z" />
  </Icon>
);

export const CatalogIcon = (props: IconProps) => (
  <Icon {...props}>
    <path d="M4 6a2 2 0 0 1 2-2h5v16H6a2 2 0 0 1-2-2z" />
    <path d="M13 4h5a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2h-5z" />
  </Icon>
);

/** Sliders rather than a gear — fewer curves to get wrong at 20px. */
export const SettingsIcon = (props: IconProps) => (
  <Icon {...props}>
    <path d="M5 21V14M5 10V3M12 21V12M12 8V3M19 21V16M19 12V3" />
    <path d="M2 14h6M9 8h6M16 16h6" />
  </Icon>
);

export const MenuIcon = (props: IconProps) => (
  <Icon {...props}>
    <path d="M4 6h16M4 12h16M4 18h16" />
  </Icon>
);

export const CloseIcon = (props: IconProps) => (
  <Icon {...props}>
    <path d="M6 6l12 12M18 6l-12 12" />
  </Icon>
);

export const ChevronDownIcon = (props: IconProps) => (
  <Icon {...props}>
    <path d="m6 9 6 6 6-6" />
  </Icon>
);

export const CheckIcon = (props: IconProps) => (
  <Icon {...props}>
    <path d="m5 13 4 4L19 7" />
  </Icon>
);
