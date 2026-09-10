using TripsAgent.Domain.Tenancy;

namespace TripsAgent.Application.Tenancy;

/// <summary>Whether an agency may put money into its wallet, and if not, what to tell them.</summary>
/// <param name="IsAllowed">Whether funding may proceed.</param>
/// <param name="Reason">
/// Plain-English explanation when it may not. Written to be shown to the agent as-is.
/// </param>
public sealed record WalletFundingDecision(bool IsAllowed, string? Reason)
{
    public static WalletFundingDecision Allowed { get; } = new(true, null);

    public static WalletFundingDecision Denied(string reason) => new(false, reason);
}

/// <summary>
/// The rule that an unverified agency cannot fund its wallet. FRD §2.2.
/// </summary>
/// <remarks>
/// <para>
/// A named policy rather than an <c>if</c> inside a payment handler, because the same rule has to
/// be answered in two very different places: the wallet endpoint has to <i>enforce</i> it, and the
/// onboarding screens have to <i>explain</i> it — the FRD asks for the funding option to be
/// visibly disabled with a reason, not to fail silently when pressed.
/// </para>
/// <para>
/// The wallet itself arrives later in M1. This exists now so that when it does, the rule is
/// already written, tested, and impossible to overlook.
/// </para>
/// </remarks>
public static class WalletFundingPolicy
{
    public static WalletFundingDecision For(Agency agency)
    {
        ArgumentNullException.ThrowIfNull(agency);

        return agency.Status switch
        {
            AgencyStatus.Verified => WalletFundingDecision.Allowed,

            AgencyStatus.PendingVerification => WalletFundingDecision.Denied(
                "You can add funds once your business is verified. Upload your KYB documents to "
                + "get started — most reviews are finished within one working day."),

            AgencyStatus.Rejected => WalletFundingDecision.Denied(
                "Your verification was not approved. Check the reason on your onboarding page, "
                + "correct the documents and submit again."),

            AgencyStatus.Suspended => WalletFundingDecision.Denied(
                "This account is suspended, so funds cannot be added. Contact Trips support."),

            AgencyStatus.Terminated => WalletFundingDecision.Denied(
                "This account is closed and cannot be funded."),

            _ => WalletFundingDecision.Denied("This account cannot be funded at the moment."),
        };
    }
}
