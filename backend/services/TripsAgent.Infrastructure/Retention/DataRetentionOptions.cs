namespace TripsAgent.Infrastructure.Retention;

/// <summary>
/// How long each kind of operational row is kept, and whether the purge job may actually delete.
/// </summary>
/// <remarks>
/// Windows are whole days, counted back from the start of the day the job runs. Financial records and
/// the audit log have no setting here on purpose: they are never this job's to touch, so there is
/// nothing to configure. The table in <c>docs/DATA_RETENTION.md</c> gives the reasoning for each default.
/// </remarks>
public sealed class DataRetentionOptions
{
    /// <summary>Configuration section this binds to.</summary>
    public const string SectionName = "DataRetention";

    /// <summary>
    /// The inbox is what stops a redelivered message from being processed twice. Forgetting a message
    /// sooner than a broker could plausibly redeliver it would reopen that door.
    /// </summary>
    public const int MinimumProcessedMessageDays = 7;

    /// <summary>
    /// True until someone deliberately sets it false. In a dry run the job counts and records what it
    /// would delete, and deletes nothing — that is how a wrong <c>WHERE</c> clause is found before it
    /// costs a customer their booking history.
    /// </summary>
    public bool DryRun { get; set; } = true;

    /// <summary><c>identity.login_attempts</c>, from the attempt.</summary>
    public int LoginAttemptDays { get; set; } = 90;

    /// <summary>Refresh tokens, password reset tokens and verification codes, from their expiry.</summary>
    public int ExpiredCredentialDays { get; set; } = 30;

    /// <summary>Invitations that were never accepted, from their expiry.</summary>
    public int ExpiredInvitationDays { get; set; } = 90;

    /// <summary>Outbox messages from dispatch, inbox records from processing.</summary>
    public int ProcessedMessageDays { get; set; } = 30;

    /// <summary>Sent, delivered, failed and bounced notifications, from when they were queued.</summary>
    public int NotificationDays { get; set; } = 365;

    /// <summary>Carts that never became an order, from their expiry.</summary>
    public int ExpiredCartDays { get; set; } = 30;

    /// <summary>Passport and visa details, from the end of the trip they were collected for.</summary>
    public int TravelDocumentDays { get; set; } = 90;

    /// <summary>Every window, for validation.</summary>
    internal IEnumerable<int> Windows() =>
    [
        LoginAttemptDays,
        ExpiredCredentialDays,
        ExpiredInvitationDays,
        ProcessedMessageDays,
        NotificationDays,
        ExpiredCartDays,
        TravelDocumentDays,
    ];
}
