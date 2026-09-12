namespace TripsAgent.Domain.Tenancy;

/// <summary>
/// What an agency in a given <see cref="AgencyStatus"/> may still do.
/// </summary>
/// <remarks>
/// <para>
/// One place, so suspension means the same thing in the checkout, on the storefront and in the
/// console. Build-plan decision 14 settles what that is, and it is narrower than "switch them
/// off": a suspended agency has already sold tickets that people are going to fly on.
/// </para>
/// <list type="bullet">
///   <item><b>Existing bookings stand.</b> Nothing is cancelled, nothing is refunded.</item>
///   <item><b>Travellers keep their documents.</b> The magic link on their booking goes on working.</item>
///   <item><b>Support services them.</b> Trips staff can still read and act on the agency's orders.</item>
///   <item><b>No new bookings.</b> Checkout refuses, in the console and on the storefront.</item>
///   <item><b>The storefront goes offline.</b> The public site stops serving (FRD §2.15 RS-3).</item>
/// </list>
/// <para>
/// Plain static methods over an enum rather than behaviour on <see cref="Agency"/>: the callers
/// that matter most — host resolution, the checkout guard — have the status in hand and no
/// reason to load the whole aggregate.
/// </para>
/// </remarks>
public static class AgencyAccess
{
    /// <summary>May this agency start a new booking, through any channel?</summary>
    /// <remarks>
    /// Only a verified agency. Pending and rejected agencies have never been allowed to transact
    /// (FRD §2.2); suspended and terminated ones no longer are.
    /// </remarks>
    public static bool CanTakeNewBookings(AgencyStatus status) => status == AgencyStatus.Verified;

    /// <summary>Should this agency's public storefront answer a traveller's request?</summary>
    public static bool CanServeStorefront(AgencyStatus status) => status == AgencyStatus.Verified;

    /// <summary>
    /// May a traveller still reach the documents for a booking they already hold?
    /// </summary>
    /// <remarks>
    /// Yes for everything except a terminated agency, whose data has been exported and whose
    /// relationship with us is over. Somebody who paid for a ticket keeps their ticket while the
    /// agency's standing is being argued about.
    /// </remarks>
    public static bool CanServeExistingTravellers(AgencyStatus status) =>
        status != AgencyStatus.Terminated;

    /// <summary>May staff at this agency sign in to the agent console?</summary>
    /// <remarks>
    /// A suspended agency's staff can still sign in and read: they have travellers mid-journey to
    /// look after, and locking them out makes that Trips' problem instead of theirs. What they
    /// cannot do is sell, which <see cref="CanTakeNewBookings"/> is the rule for.
    /// </remarks>
    public static bool CanSignIn(AgencyStatus status) => status != AgencyStatus.Terminated;

    /// <summary>
    /// One sentence for whoever is refused, or null when nothing is being refused.
    /// </summary>
    /// <remarks>
    /// Written for an agency reading it in their own console. It never says which admin did it or
    /// why — the reason is internal, and a traveller-facing surface must never repeat it.
    /// </remarks>
    public static string? WhyNewBookingsAreRefused(AgencyStatus status) => status switch
    {
        AgencyStatus.Verified => null,
        AgencyStatus.PendingVerification =>
            "This account is still being verified. You can prepare, but not yet sell.",
        AgencyStatus.Rejected =>
            "Verification was refused. Correct your KYB submission and send it again.",
        AgencyStatus.Suspended =>
            "This account is suspended. Bookings already made are unaffected; new ones are paused "
            + "until the suspension is lifted. Contact Trips support.",
        AgencyStatus.Terminated =>
            "This account is closed.",
        _ => "This account cannot sell at the moment.",
    };
}
