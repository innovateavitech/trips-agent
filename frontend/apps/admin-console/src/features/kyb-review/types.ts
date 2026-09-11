/**
 * The shapes the KYB review screens read — mirrors of the records in
 * backend/services/TripsAgent.Contracts/Tenancy/KybContracts.cs, camel-cased as the API sends them.
 *
 * Hand-written for now, like the wallet feature's. When `pnpm generate:api` produces
 * `@trips/api-client` from the C# DTOs, delete this file and import from there: two hand-kept
 * copies of one shape is how a field quietly changes meaning on one side only.
 */

/** KybSubmissionStatus in the domain. Only `Submitted` and `UnderReview` reach the queue. */
export type KybSubmissionStatus = 'Draft' | 'Submitted' | 'UnderReview' | 'Approved' | 'Rejected';

/** AgencyStatus in the domain. */
export type AgencyStatus =
  'PendingVerification' | 'Verified' | 'Rejected' | 'Suspended' | 'Terminated';

/** KybQueueItemResponse — one row of the queue. */
export interface KybQueueItem {
  submissionId: string;
  agencyId: string;
  /** Trading name when the agency gave one, legal name otherwise. */
  agencyName: string;
  /** ISO 3166-1 alpha-2, e.g. `NG`. */
  countryCode: string;
  status: KybSubmissionStatus;
  /** ISO 8601. */
  submittedAt: string | null;
  documentCount: number;
}

/** KybReviewDocumentResponse — one file, behind a signed link that expires. */
export interface KybReviewDocument {
  id: string;
  /** KybDocumentType, e.g. `CertificateOfIncorporation`. */
  documentType: string;
  fileName: string;
  /** Established by sniffing the file's bytes on upload, not what the browser claimed. */
  contentType: string;
  sizeBytes: number;
  /** A relative, signed path. It IS the credential, so it is never logged or shared. */
  url: string;
  /** When `url` stops working — ten minutes after the detail was fetched. */
  urlExpiresAt: string;
}

/** KybReviewDetailResponse — everything needed to decide one submission. */
export interface KybReviewDetail {
  submissionId: string;
  agencyId: string;
  legalName: string;
  tradingName: string | null;
  countryCode: string;
  agencyStatus: AgencyStatus;
  submissionStatus: KybSubmissionStatus;
  submittedAt: string | null;
  /** Set only on a rejected submission. */
  rejectionReason: string | null;
  documents: KybReviewDocument[];
}
