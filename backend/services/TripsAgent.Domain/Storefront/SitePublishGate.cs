namespace TripsAgent.Domain.Storefront;

/// <summary>What the publish gate needs to know, gathered by the application before it asks.</summary>
/// <param name="AgencyIsVerified">KYB is approved, so the agency may trade (FRD §2.2).</param>
/// <param name="PublishedProductCount">Tours, packages and visas the agency has published.</param>
/// <param name="FlightSearchEnabled">The site offers the agency's flights.</param>
public sealed record PublishGateFacts(bool AgencyIsVerified, int PublishedProductCount, bool FlightSearchEnabled);

/// <summary>One reason a site cannot be published yet, written for the agent.</summary>
/// <param name="Code">Stable, for the console to key on: <c>agency-not-verified</c>, <c>nothing-to-sell</c>.</param>
/// <param name="Message">What to do about it.</param>
public sealed record PublishProblem(string Code, string Message);

/// <summary>
/// Whether a site may go live. Rolling back is never asked: it is not a publish.
/// </summary>
public static class SitePublishGate
{
    public const string AgencyNotVerified = "agency-not-verified";

    public const string NothingToSell = "nothing-to-sell";

    /// <summary>Every reason the site cannot be published, or none.</summary>
    public static IReadOnlyList<PublishProblem> Check(PublishGateFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var problems = new List<PublishProblem>();

        // A public site under our infrastructure, before we know the business is real, is how a
        // phishing page gets a respectable address. The same line KYB draws around the wallet.
        if (!facts.AgencyIsVerified)
        {
            problems.Add(new PublishProblem(
                AgencyNotVerified,
                "Your business has not been verified yet. Your site can go live once verification is approved."));
        }

        if (!SomethingToSellRule.IsSatisfiedBy(facts))
        {
            problems.Add(new PublishProblem(
                NothingToSell,
                "Publish at least one tour, package or visa first — or turn on flight sales for your site in its settings."));
        }

        return problems;
    }
}

/// <summary>
/// A site must have something to sell before it goes live: at least one published product, <b>or</b>
/// flight sales switched on for the site.
/// </summary>
/// <remarks>
/// <para>
/// This is open question 11, decided the way the plan recommends. FRD §2.11 RS-6 blocks publishing
/// until a tour, visa or group tour is published — which means an agency whose whole business is
/// flight ticketing could never go live. The rule is relaxed to accept flight sales as something to
/// sell.
/// </para>
/// <para>
/// One class, so that if the client answers the question differently, this is the only place that
/// changes.
/// </para>
/// </remarks>
public static class SomethingToSellRule
{
    public static bool IsSatisfiedBy(PublishGateFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        return facts.PublishedProductCount > 0 || facts.FlightSearchEnabled;
    }
}
