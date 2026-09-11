/**
 * The KYB step, as the API describes it.
 *
 * Mirrors `TripsAgent.Contracts.Tenancy.KybContracts`. The generated client types the responses;
 * these names give the screens something readable to hold, and TypeScript checks the two agree
 * wherever a response is assigned to one.
 */

/** Where a submission has got to. */
export type SubmissionStatus = 'Draft' | 'Submitted' | 'UnderReview' | 'Approved' | 'Rejected';

/** Where the agency has got to. Only `Verified` may take payments. */
export type AgencyStatus =
  'PendingVerification' | 'Verified' | 'Rejected' | 'Suspended' | 'Terminated';

export interface KybDocument {
  id: string;
  documentType: string;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  uploadedAt: string;
}

export interface KybStatus {
  submissionId: string | null;
  status: SubmissionStatus;
  agencyStatus: AgencyStatus;
  submittedAt: string | null;
  reviewedAt: string | null;

  /** The reviewer's own words, shown verbatim. Null unless rejected. */
  rejectionReason: string | null;

  /** Whether documents may still be added or replaced. */
  canEdit: boolean;

  documents: KybDocument[];

  /** Required documents not yet attached. Empty means the submission is complete. */
  missingDocumentTypes: string[];

  canFundWallet: boolean;

  /** Why funding is blocked, written for the agent. Null when it is allowed. */
  walletFundingBlockedReason: string | null;
}

/** What the upload control enforces before a byte leaves the browser. */
export interface UploadLimits {
  maxSizeBytes: number;
  allowedContentTypes: string[];
  allowedExtensions: string[];
  requiredDocumentTypes: string[];
}
