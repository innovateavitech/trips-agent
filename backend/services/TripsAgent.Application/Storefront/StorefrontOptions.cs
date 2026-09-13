namespace TripsAgent.Application.Storefront;

/// <summary>Where agencies' websites live, and how the builder writes their addresses.</summary>
public sealed class StorefrontOptions
{
    public const string SectionName = "Storefront";

    /// <summary>The longest a preview link may work (the website builder's breakdown, W7).</summary>
    public static TimeSpan MaxPreviewLinkLifetime { get; } = TimeSpan.FromHours(24);

    /// <summary>
    /// The zone free addresses live under: <c>agency.&lt;this&gt;</c>. Travellers see it in their address
    /// bar, so it must be a neutral domain that does not name the platform (CLAUDE.md rule 4).
    /// <c>localhost</c> for local work, where browsers send every <c>*.localhost</c> name to this machine.
    /// </summary>
    public string SubdomainBaseDomain { get; init; } = "localhost";

    /// <summary>What an agency's own hostname points its CNAME record at. Neutral, for the same reason.</summary>
    public string CustomDomainTarget { get; init; } = "sites.localhost";

    /// <summary>How a site's address is written, with <c>{host}</c> for its hostname.</summary>
    public string SiteUrlFormat { get; init; } = "https://{host}";

    /// <summary>How long a preview link works.</summary>
    public TimeSpan PreviewLinkLifetime { get; init; } = TimeSpan.FromHours(12);

    /// <summary>The address of the site on <paramref name="hostname"/>, with no trailing slash.</summary>
    public string SiteUrlFor(string hostname)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);

        return SiteUrlFormat.Replace("{host}", hostname, StringComparison.Ordinal).TrimEnd('/');
    }
}
