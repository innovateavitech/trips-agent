/**
 * The shapes the audit viewer reads — mirrors of AuditLogEntryResponse and AuditLogPageResponse in
 * backend/services/TripsAgent.Contracts/Platform/AgencyAdminContracts.cs.
 *
 * Hand-written for the same reason the agency screens' are, and with the same debt: when the
 * console moves onto the generated `@trips/api-client`, delete this file and import from there.
 */

/** AuditLogEntryResponse — one thing somebody did. */
export interface AuditLogEntry {
  id: string;
  occurredAt: string;
  /** Null on a platform action: an admin's row belongs to no agency. */
  agencyId: string | null;
  agencyName: string | null;
  actorUserId: string | null;
  /** The person's name; the API resolves "System" and "Anonymous" when there is no person. */
  actorName: string;
  /** AuditActorType: User, PlatformAdmin, System, Anonymous or Unknown. */
  actorType: string;
  /** `agency.suspended`, `created`, `platform_user.role_changed` … */
  action: string;
  entityType: string;
  entityId: string;
  /** Why, when the action demanded one. Internal — never shown to a traveller. */
  reason: string | null;
  /** The recorded columns, as JSON text. Null on an insert. */
  beforeState: string | null;
  afterState: string | null;
  actorIpAddress: string | null;
}

/** AuditLogPageResponse — one page, newest first. */
export interface AuditLogPage {
  items: AuditLogEntry[];
  totalCount: number;
  page: number;
  pageSize: number;
}

/** What the viewer is asking for. Every field is optional. */
export interface AuditLogQuery {
  agencyId?: string;
  actorUserId?: string;
  action?: string;
  entityType?: string;
  entityId?: string;
  /** ISO instants. The screen sends UTC; the API converts anything else. */
  from?: string;
  to?: string;
  page?: number;
  pageSize?: number;
}
