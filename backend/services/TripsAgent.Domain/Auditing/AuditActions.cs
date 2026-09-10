namespace TripsAgent.Domain.Auditing;

/// <summary>
/// The action names written automatically when an audited entity is saved.
///
/// Business actions carry their own name instead — <c>agency.suspended</c>,
/// <c>kyb.rejected</c>, <c>wallet.manually_adjusted</c> — because "updated" does not answer the
/// question anyone is actually asking six months later. Use lower_snake_case, and prefix with
/// the thing acted on.
/// </summary>
public static class AuditActions
{
    /// <summary>The row was inserted.</summary>
    public const string Created = "created";

    /// <summary>One or more columns changed. The detail is in the before/after state.</summary>
    public const string Updated = "updated";

    /// <summary>The row was deleted.</summary>
    public const string Deleted = "deleted";
}
