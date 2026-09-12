import type { SVGProps } from 'react';

/**
 * The handful of icons the console uses, drawn once in one style: a 24-unit grid, 1.75 stroke,
 * round caps, and `currentColor` so every icon takes its colour from the text around it (and so
 * from the design tokens — there is no colour in this file).
 *
 * Hand-drawn rather than a dependency because we need a dozen, not eight hundred. When the console
 * needs many more, swap this file for a proper icon library in one go rather than mixing the two.
 */
type IconProps = SVGProps<SVGSVGElement>;

function Icon({ children, className = 'h-4 w-4', ...props }: IconProps) {
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
      className={className}
      {...props}
    >
      {children}
    </svg>
  );
}

/** The KYB queue: a stack of papers waiting. */
export function QueueIcon(props: IconProps) {
  return (
    <Icon {...props}>
      <path d="M4 7h16" />
      <path d="M4 12h16" />
      <path d="M4 17h10" />
    </Icon>
  );
}

export function DocumentIcon(props: IconProps) {
  return (
    <Icon {...props}>
      <path d="M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z" />
      <path d="M14 3v5h5" />
      <path d="M9 13h6" />
      <path d="M9 17h4" />
    </Icon>
  );
}

export function ExternalIcon(props: IconProps) {
  return (
    <Icon {...props}>
      <path d="M14 4h6v6" />
      <path d="M20 4l-9 9" />
      <path d="M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5" />
    </Icon>
  );
}

export function ArrowLeftIcon(props: IconProps) {
  return (
    <Icon {...props}>
      <path d="M19 12H5" />
      <path d="M11 6l-6 6 6 6" />
    </Icon>
  );
}

export function RefreshIcon(props: IconProps) {
  return (
    <Icon {...props}>
      <path d="M20 11a8 8 0 0 0-14.3-4.9L4 8" />
      <path d="M4 4v4h4" />
      <path d="M4 13a8 8 0 0 0 14.3 4.9L20 16" />
      <path d="M20 20v-4h-4" />
    </Icon>
  );
}

export function ChevronDownIcon(props: IconProps) {
  return (
    <Icon {...props}>
      <path d="M6 9l6 6 6-6" />
    </Icon>
  );
}

export function SignOutIcon(props: IconProps) {
  return (
    <Icon {...props}>
      <path d="M15 4h3a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2h-3" />
      <path d="M10 17l-5-5 5-5" />
      <path d="M5 12h11" />
    </Icon>
  );
}

export function CheckIcon(props: IconProps) {
  return (
    <Icon {...props}>
      <path d="M5 12.5l4.5 4.5L19 7.5" />
    </Icon>
  );
}

/** The operations dashboard: a gauge. */
export function DashboardIcon(props: IconProps) {
  return (
    <Icon {...props}>
      <path d="M3 13a9 9 0 0 1 18 0" />
      <path d="M12 13 16 9" />
      <path d="M3 13h2M19 13h2M12 4v2" />
    </Icon>
  );
}

/** The agency directory: a building. */
export function BuildingIcon(props: IconProps) {
  return (
    <Icon {...props}>
      <path d="M4 21V5a1 1 0 0 1 1-1h9a1 1 0 0 1 1 1v16" />
      <path d="M15 9h4a1 1 0 0 1 1 1v11" />
      <path d="M2 21h20M8 8h3M8 12h3M8 16h3" />
    </Icon>
  );
}

/** Back-office users: two people. */
export function PeopleIcon(props: IconProps) {
  return (
    <Icon {...props}>
      <path d="M16 19v-1a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v1" />
      <circle cx="9" cy="7" r="3.25" />
      <path d="M22 19v-1a4 4 0 0 0-3-3.87M16.5 4.2a3.25 3.25 0 0 1 0 5.6" />
    </Icon>
  );
}

/** The audit trail: a list with a clock on it. */
export function HistoryIcon(props: IconProps) {
  return (
    <Icon {...props}>
      <path d="M3.5 9a9 9 0 1 1 .6 6" />
      <path d="M3 4v5h5" />
      <path d="M12 8v4.5l3 1.8" />
    </Icon>
  );
}

/** A warning, for a suspended agency and a critical alert. */
export function AlertIcon(props: IconProps) {
  return (
    <Icon {...props}>
      <path d="M12 4.5 2.8 20a1 1 0 0 0 .87 1.5h16.66A1 1 0 0 0 21.2 20Z" />
      <path d="M12 10v4.5M12 18h.01" />
    </Icon>
  );
}

/** Searching the directory. */
export function SearchIcon(props: IconProps) {
  return (
    <Icon {...props}>
      <circle cx="11" cy="11" r="7" />
      <path d="m20 20-3.5-3.5" />
    </Icon>
  );
}

/** Taking an export away with you. */
export function DownloadIcon(props: IconProps) {
  return (
    <Icon {...props}>
      <path d="M12 3v12" />
      <path d="m7.5 10.5 4.5 4.5 4.5-4.5" />
      <path d="M4 19h16" />
    </Icon>
  );
}
