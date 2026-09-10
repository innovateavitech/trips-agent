namespace TripsAgent.Domain.Auditing;

/// <summary>
/// Who performed an audited action. Stored so "the system did it" can never be confused with
/// "a person did it", which is the first question asked when something looks wrong.
/// </summary>
public enum AuditActorType
{
    /// <summary>No actor could be determined. Should be rare; treat rows like this as a bug.</summary>
    Unknown = 0,

    /// <summary>A signed-in user of a travel agency, acting within their own agency.</summary>
    User = 1,

    /// <summary>A Trips back-office user. These are the actions FRD §2.15 RS-5 is really about.</summary>
    PlatformAdmin = 2,

    /// <summary>A background job, saga or migration. No human pressed anything.</summary>
    System = 3,

    /// <summary>An unauthenticated caller — a storefront visitor checking out as a guest.</summary>
    Anonymous = 4,
}
