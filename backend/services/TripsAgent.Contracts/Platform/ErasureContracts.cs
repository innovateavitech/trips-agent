namespace TripsAgent.Contracts.Platform;

// Erasing a person, as the admin console sees it (issue 106). Nothing here carries a document number
// or a bank account: those columns are encrypted and are not readable through any API.

/// <summary>Ask what erasing this person would do, before doing it.</summary>
/// <param name="AgencyId">The agency whose customer they are.</param>
/// <param name="Email">The address they gave that agency. The only way to find them.</param>
public sealed record ErasurePreviewRequest(Guid AgencyId, string Email);

/// <summary>What would be erased, and what stands in the way.</summary>
/// <param name="CustomerId">Pass this back to carry the erasure out.</param>
/// <param name="Name">Their name as it stands, so the operator can see they have the right person.</param>
/// <param name="Email">Their email as it stands.</param>
/// <param name="Orders">Orders that stay — with their money — and stop naming anyone.</param>
/// <param name="Travellers">Traveller rows whose names and travel documents go.</param>
/// <param name="TravelDocuments">Travel documents deleted outright.</param>
/// <param name="Notifications">Messages whose recipient is replaced.</param>
/// <param name="EvidenceFiles">Uploaded dispute evidence deleted from storage.</param>
/// <param name="Blockers">What must finish first. Empty means the erasure can go ahead.</param>
public sealed record ErasurePreviewResponse(
    Guid CustomerId,
    string Name,
    string? Email,
    int Orders,
    int Travellers,
    int TravelDocuments,
    int Notifications,
    int EvidenceFiles,
    IReadOnlyList<string> Blockers);

/// <summary>Carry out the erasure. There is no undo and no copy.</summary>
/// <param name="AgencyId">The agency whose customer they are.</param>
/// <param name="CustomerId">From the preview.</param>
/// <param name="Reason">Why: their request, a regulator's direction, a court order. Recorded, and required.</param>
public sealed record ErasureRequestCommand(Guid AgencyId, Guid CustomerId, string Reason);

/// <summary>What the erasure did, or why it was refused.</summary>
/// <param name="RequestId">The record of it, which survives the erasure.</param>
/// <param name="Completed">False when it was refused.</param>
/// <param name="Changed">Rows changed, by table. Counts only.</param>
/// <param name="Blockers">Why it was refused, when it was.</param>
public sealed record ErasureResultResponse(
    Guid RequestId,
    bool Completed,
    IReadOnlyDictionary<string, int> Changed,
    IReadOnlyList<string> Blockers);

/// <summary>One erasure, as the list shows it. Carries no personal detail, by design.</summary>
/// <param name="Id">The request.</param>
/// <param name="AgencyId">Whose customer it was.</param>
/// <param name="CustomerId">The row that was anonymised. It still exists; it names nobody.</param>
/// <param name="Status">Requested, Completed or Refused.</param>
/// <param name="Reason">Why it was asked for.</param>
/// <param name="RequestedByUserId">The Trips staff member who ran it.</param>
/// <param name="RequestedAt">When it was recorded.</param>
/// <param name="CompletedAt">When it finished, either way.</param>
/// <param name="Outcome">The counts, as JSON, exactly as they were recorded.</param>
/// <param name="RefusalReason">Why nothing was changed, when nothing was.</param>
public sealed record ErasureRequestResponse(
    Guid Id,
    Guid AgencyId,
    Guid CustomerId,
    string Status,
    string Reason,
    Guid? RequestedByUserId,
    DateTimeOffset RequestedAt,
    DateTimeOffset? CompletedAt,
    string? Outcome,
    string? RefusalReason);
