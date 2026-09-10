namespace TripsAgent.Infrastructure.Auditing;

/// <summary>What the audit log is allowed to record about one property.</summary>
public enum AuditFieldTreatment
{
    /// <summary>Recorded as-is. The default for ordinary business data.</summary>
    Keep = 0,

    /// <summary>
    /// Recorded with all but the last few characters replaced. Enough to confirm which document
    /// or account a row refers to, not enough to be the document or account.
    /// </summary>
    Mask = 1,

    /// <summary>Never recorded at all. The value is replaced with a fixed placeholder.</summary>
    Redact = 2,
}
