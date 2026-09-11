namespace TripsAgent.Infrastructure.Persistence;

/// <summary>
/// PostgreSQL schema names, one per bounded context (plan §2). Written once here so a typo cannot
/// quietly create a second schema.
/// </summary>
public static class Schemas
{
    /// <summary>
    /// Plumbing that belongs to no single agency: the outbox and the inbox today; feature flags and
    /// system settings later.
    /// </summary>
    public const string Platform = "platform";

    /// <summary>Notifications, their templates and the suppression list (plan §2.12).</summary>
    public const string Notifications = "notifications";
}
