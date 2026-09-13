/**
 * ============================================================================
 *  The sub-agent network, as the console sees it (feature F10, issue 63).
 * ============================================================================
 *
 * A principal invites travel businesses beneath it, says what each may sell and
 * whether it sees margins, and caps what each may spend out of the principal's
 * money. Two levels only — a sub-agent has no network of its own, and these
 * screens are hidden from one entirely.
 *
 * Every amount is minor units (kobo). Never a float, ever.
 */

/** Where a sub-agent stands. The same lifecycle every agency has. */
export type SubAgentStatus =
  | 'PendingVerification'
  | 'Verified'
  | 'Rejected'
  /** Frozen by its principal: it can sign in and read, but not sell. */
  | 'Suspended'
  /** Revoked. The relationship is over; the record stays. */
  | 'Terminated';

export interface SubAgent {
  id: string;
  legalName: string;
  tradingName: string | null;
  slug: string;
  status: SubAgentStatus;
  /** Why it was frozen or ended, in the principal's own words. */
  statusReason: string | null;
  createdAt: string;
  /** True while its first user has not accepted the invitation yet. */
  hasOpenInvitation: boolean;
  /** How many product types and suppliers it may sell. Zero means it can sell nothing. */
  scopeCount: number;
  deniedPermissionCount: number;
  /** False when its principal has taken `margin.view` away. */
  canSeeMargin: boolean;
  allowanceSpentMinor: number | null;
  allowanceLimitMinor: number | null;
  allowanceCurrency: string | null;
}

export interface SubAgentNetwork {
  subAgents: SubAgent[];
  /** The plan's cap on sub-agents, or null when the plan does not cap it. */
  maxSubAgents: number | null;
  canAddAnother: boolean;
  /** One sentence for the agent when another is not allowed. */
  cannotAddReason: string | null;
}

export interface InviteSubAgent {
  legalName: string;
  tradingName: string | null;
  email: string;
}

/**
 * What came back from an invitation.
 *
 * `invitationToken` is shown once and never again — only its hash is stored. It
 * is here so the console can offer a copyable link when the email does not
 * arrive, not as a second way in.
 */
export interface SubAgentInvited {
  id: string;
  email: string;
  invitationToken: string;
  expiresAt: string;
}

/** What a sub-agent may sell. No rows at all means it may sell nothing. */
export type SellableProductType = 'Flight' | 'Bus' | 'Tour' | 'Visa' | 'Package';

export interface SubAgentScope {
  id: string;
  productType: SellableProductType;
  /** Null means every supplier of that product type. */
  supplierId: string | null;
  supplierName: string | null;
}

/** One row of the permissions matrix. */
export interface SubAgentPermission {
  code: string;
  category: string;
  description: string;
  isDenied: boolean;
  reason: string | null;
}

export type AllowancePeriod = 'Lifetime' | 'Daily' | 'Weekly' | 'Monthly';

export type AllowanceStatus = 'Active' | 'Frozen';

export interface Allowance {
  subAgencyId: string;
  currency: string;
  spentMinor: number;
  limitMinor: number;
  /** What is left this period. Never negative, even after the cap is lowered. */
  remainingMinor: number;
  period: AllowancePeriod;
  status: AllowanceStatus;
  resetsAt: string | null;
}

/**
 * One agency's part of the network's figures.
 *
 * `marginMinor` is `null` when the response carried no margin at all. The API
 * sends two different shapes — with margin and without — so this is driven by
 * what arrived, never by a permission check in the browser.
 */
export interface NetworkMember {
  agencyId: string;
  name: string;
  status: SubAgentStatus;
  isPrincipal: boolean;
  orders: number;
  salesMinor: number;
  marginMinor: number | null;
  allowanceSpentMinor: number | null;
  allowanceLimitMinor: number | null;
}

export interface NetworkPerformance {
  from: string;
  to: string;
  currency: string;
  orders: number;
  salesMinor: number;
  /** Null when the response had no margin figures in it at all. */
  marginMinor: number | null;
  members: NetworkMember[];
}
