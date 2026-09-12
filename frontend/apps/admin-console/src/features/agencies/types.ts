/**
 * The shapes the agency screens read — mirrors of the records in
 * backend/services/TripsAgent.Contracts/Platform/AgencyAdminContracts.cs, camel-cased as the API
 * sends them.
 *
 * Hand-written for now, like the KYB review feature's. When the console is moved onto the
 * generated `@trips/api-client`, delete this file and import from there: two hand-kept copies of
 * one shape is how a field quietly changes meaning on one side only.
 */

/** AgencyStatus in the domain. */
export type AgencyStatus =
  'PendingVerification' | 'Verified' | 'Rejected' | 'Suspended' | 'Terminated';

/** AgencyType in the domain. */
export type AgencyType = 'Principal' | 'SubAgent';

/** How the directory is sorted. Matches AgencySort in the API. */
export type AgencySort = 'Newest' | 'Oldest' | 'Name';

/** AgencySummaryResponse — one row of the directory. */
export interface AgencySummary {
  id: string;
  /** Trading name when the agency gave one, legal name otherwise. */
  name: string;
  legalName: string;
  slug: string;
  status: AgencyStatus;
  type: AgencyType;
  countryCode: string;
  baseCurrency: string;
  parentAgencyId: string | null;
  parentAgencyName: string | null;
  createdAt: string;
  verifiedAt: string | null;
  /** Minor units. Null when no wallet has been opened, which is every agency before KYB. */
  walletBalanceMinor: number | null;
  orderCount: number;
}

/** AgencyDirectoryResponse — one page, with the total behind the filter. */
export interface AgencyDirectory {
  items: AgencySummary[];
  totalCount: number;
  page: number;
  pageSize: number;
}

/** AgencyUserResponse — one person at an agency. */
export interface AgencyUser {
  id: string;
  email: string;
  fullName: string;
  status: string;
  roles: string[];
  lastLoginAt: string | null;
}

/** AgencyProfileResponse — everything the profile screen reads. */
export interface AgencyProfile {
  id: string;
  name: string;
  legalName: string;
  tradingName: string | null;
  slug: string;
  status: AgencyStatus;
  type: AgencyType;
  countryCode: string;
  baseCurrency: string;
  timezone: string;
  taxId: string | null;
  vatRateBasisPoints: number;
  parentAgencyId: string | null;
  parentAgencyName: string | null;
  createdAt: string;
  verifiedAt: string | null;
  onboardingCompletedAt: string | null;
  statusChangedAt: string | null;
  /** Why it is suspended or terminated. Internal — never shown to a traveller. */
  statusReason: string | null;
  canTakeNewBookings: boolean;
  storefrontIsLive: boolean;
  walletBalanceMinor: number | null;
  walletReservedMinor: number | null;
  orderCount: number;
  grossSalesMinor: number;
  users: AgencyUser[];
  subAgents: AgencySummary[];
}

/** What an edit sends. The reason is not optional. */
export interface UpdateAgencyRequest {
  legalName: string;
  tradingName: string | null;
  taxId: string | null;
  timezone: string;
  vatRateBasisPoints: number;
  reason: string;
}

/** AgencyStatusResponse — what the standing is after an action. */
export interface AgencyStatusChange {
  agencyId: string;
  status: AgencyStatus;
  changedAt: string;
  reason: string;
  canTakeNewBookings: boolean;
  storefrontIsLive: boolean;
}

/** The lifecycle actions, as the console names them. */
export type AgencyAction = 'verify' | 'suspend' | 'reinstate' | 'terminate';

/** What the directory is being asked for. Every field is optional. */
export interface AgencyDirectoryQuery {
  search?: string;
  status?: AgencyStatus;
  type?: AgencyType;
  sort?: AgencySort;
  page?: number;
  pageSize?: number;
}
