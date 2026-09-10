namespace TripsAgent.Domain.Tenancy;

/// <summary>
/// Where an agency is in its lifecycle. Drives what it may do and whether its storefront serves.
/// </summary>
public enum AgencyStatus
{
    /// <summary>
    /// Signed up, KYB not yet approved. Can sign in and prepare, but <b>cannot fund a wallet</b>
    /// or transact (FRD §2.2).
    /// </summary>
    PendingVerification = 1,

    /// <summary>KYB approved. Full access.</summary>
    Verified = 2,

    /// <summary>KYB rejected with a reason. May correct the submission and resubmit.</summary>
    Rejected = 3,

    /// <summary>
    /// Temporarily stopped by Trips. Suspension takes the storefront offline — FRD §2.15 RS-3
    /// requires the public site to stop serving, not merely the console to lock.
    /// </summary>
    Suspended = 4,

    /// <summary>Ended for good. Kept for the audit trail and historical orders; never deleted.</summary>
    Terminated = 5,
}
