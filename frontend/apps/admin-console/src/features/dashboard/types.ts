/**
 * Mirrors of the records in
 * backend/services/TripsAgent.Contracts/Platform/OperationsDashboardContracts.cs, camel-cased as
 * the API sends them. Hand-written for now, like the rest of the console's — see
 * features/agencies/types.ts for why, and when they go.
 */

export interface AgencyCounts {
  total: number;
  pendingVerification: number;
  verified: number;
  rejected: number;
  suspended: number;
  terminated: number;
}

/** What sold in one window, in one currency. All amounts are minor units. */
export interface SalesWindow {
  label: string;
  from: string;
  to: string;
  currency: string;
  orderCount: number;
  grossMinor: number;
  netMinor: number;
  markupMinor: number;
  platformFeeMinor: number;
}

/** One thing waiting for a person, from platform.admin_alerts. */
export interface AdminAlert {
  id: string;
  type: string;
  severity: 'Info' | 'Warning' | 'Critical';
  status: 'Open' | 'Acknowledged' | 'Resolved';
  agencyId: string | null;
  agencyName: string | null;
  entityType: string;
  entityId: string | null;
  message: string;
  createdAt: string;
}

export interface OperationsDashboard {
  /** When these numbers were counted. */
  generatedAt: string;
  /** When they will be recounted. Always inside the ten-minute staleness budget. */
  staleAfter: string;
  agencies: AgencyCounts;
  pendingKybCount: number;
  openAlertCount: number;
  criticalAlertCount: number;
  bookingsNeedingResolution: number;
  sales: SalesWindow[];
  alerts: AdminAlert[];
}
