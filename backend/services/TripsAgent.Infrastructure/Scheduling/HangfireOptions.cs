namespace TripsAgent.Infrastructure.Scheduling;

/// <summary>
/// How Hangfire is set up. Bound from the <c>Hangfire</c> section of configuration.
///
/// Hangfire is the cron half of the platform: recurring jobs on a clock. The event-driven half —
/// sagas, domain events, anything on the booking or money path — is MassTransit, and lives in
/// <see cref="Messaging.MessagingRegistration"/>. Plan §3 draws that line; keeping to it is what
/// stops the two from quietly overlapping.
/// </summary>
public sealed class HangfireOptions
{
    /// <summary>Configuration section these settings are read from.</summary>
    public const string SectionName = "Hangfire";

    /// <summary>
    /// PostgreSQL schema Hangfire keeps its own tables in. A separate schema, not <c>public</c>,
    /// so <c>\dt</c> in psql shows our tables and not Hangfire's bookkeeping.
    /// </summary>
    public string SchemaName { get; set; } = "hangfire";

    /// <summary>
    /// Threads processing jobs, per Worker process. Null uses Hangfire's default of
    /// <c>ProcessorCount * 5</c>, which is far too many for jobs that mostly wait on a database.
    /// </summary>
    public int? WorkerCount { get; set; } = 8;

    /// <summary>
    /// Whether <c>/hangfire</c> is served at all. Off by default: a dashboard nobody asked for is
    /// an attack surface nobody is watching.
    /// </summary>
    public bool DashboardEnabled { get; set; }

    /// <summary>
    /// Role a signed-in user must hold to open the dashboard. Recurring jobs move money, so this
    /// is a platform-staff role, never an agent role.
    /// </summary>
    public string DashboardRole { get; set; } = "platform-admin";

    /// <summary>
    /// Lets a request from the local machine open the dashboard without signing in.
    ///
    /// Development only, and the Api refuses to honour it outside Development. Identity does not
    /// exist yet (issue #12), so without this there would be no way to look at the dashboard at
    /// all — and the alternative, a shared password in appsettings, is the thing CLAUDE.md rule 8
    /// exists to prevent.
    /// </summary>
    public bool AllowLocalRequestsWithoutAuthentication { get; set; }
}
