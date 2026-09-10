namespace TripsAgent.Infrastructure.Auditing;

/// <summary>
/// How long audit history is kept, and how far ahead partitions are prepared.
///
/// <para>
/// <b>The retention figure is not settled.</b> Open question 26 in the delivery plan sets a
/// seven-year hold on financial and audit records against the NDPA 2023 right to erasure, and
/// records that resolving it needs Nigerian legal counsel rather than an engineering decision.
/// The default here is that seven years, expressed as a number somebody can change in
/// configuration the day the answer arrives — no migration, no deploy of new code.
/// </para>
/// </summary>
public sealed class AuditLogOptions
{
    /// <summary>Configuration section this binds to.</summary>
    public const string SectionName = "AuditLog";

    /// <summary>
    /// Months of history to keep. Partitions older than this are dropped whole.
    /// Defaults to 84 — seven years. See the note on this type before changing it.
    /// </summary>
    public int RetentionMonths { get; set; } = 84;

    /// <summary>
    /// How many future months to keep partitions ready for.
    ///
    /// More than one on purpose: a missing partition means every insert fails, which would take
    /// the whole application down at midnight on the first of a month. Three months of runway
    /// means the maintenance job can fail silently for a quarter before anyone is woken up.
    /// </summary>
    public int PartitionsCreatedAhead { get; set; } = 3;
}
