/**
 * The shapes the erasure screen reads and writes — mirrors of the records in
 * backend/services/TripsAgent.Contracts/Platform/ErasureContracts.cs (issue 106).
 *
 * Hand-written, like the other back-office features', and with the same debt: when the console
 * moves onto the generated `@trips/api-client`, delete this file and import from there.
 */

/** ErasurePreviewResponse — what erasing this person would do, and what stands in the way. */
export interface ErasurePreview {
  customerId: string;
  /** Their name as it stands, so the operator can see they have the right person. */
  name: string;
  email: string | null;
  /** Orders that stay, with their money, and stop naming anyone. */
  orders: number;
  travellers: number;
  travelDocuments: number;
  notifications: number;
  evidenceFiles: number;
  /** What must finish first. Empty means it can go ahead. */
  blockers: string[];
}

/** ErasureResultResponse — what it did, or why it was refused. */
export interface ErasureResult {
  requestId: string;
  completed: boolean;
  /** Rows changed, by table. Counts only — never what they held. */
  changed: Record<string, number>;
  blockers: string[];
}

/** ErasureRequestResponse — one erasure, as the list shows it. Carries no personal detail. */
export interface ErasureRequestRecord {
  id: string;
  agencyId: string;
  customerId: string;
  /** Requested, Completed or Refused. */
  status: string;
  reason: string;
  requestedByUserId: string | null;
  requestedAt: string;
  completedAt: string | null;
  /** The counts, as JSON text, exactly as they were recorded. */
  outcome: string | null;
  refusalReason: string | null;
}
