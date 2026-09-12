using System.Text.Json;
using TripsAgent.Contracts.Storefront;
using TripsAgent.Domain.Storefront;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.Application.Storefront;

/// <summary>
/// Freezes a site's draft into the versioned JSON the storefront renders, and reads it back.
/// </summary>
/// <remarks>
/// <para>
/// The format is the contract between the builder and the storefront (<see cref="SiteContentSnapshot"/>),
/// pinned by a golden-file test so that changing it is always a decision.
/// </para>
/// <para>
/// Compare snapshots with <see cref="Canonical{T}"/>, never as stored text: PostgreSQL keeps jsonb in its
/// own key order, so the text read back is not the text written, even when nothing changed.
/// </para>
/// </remarks>
public static class SiteSnapshots
{
    /// <summary>The shape written today. A reader must keep understanding every older one.</summary>
    public const int SchemaVersion = 1;

    /// <summary>The networks a site may link to, in the order the footer shows them.</summary>
    public static IReadOnlyList<string> SocialNetworks { get; } = ["instagram", "facebook", "x", "tiktok", "youtube", "linkedin"];

    /// <summary>The draft's pages and settings, as the storefront will render them.</summary>
    public static SiteContentSnapshot BuildContent(Site site, AgencyBranding branding, IEnumerable<SitePage> pages)
    {
        ArgumentNullException.ThrowIfNull(site);
        ArgumentNullException.ThrowIfNull(branding);
        ArgumentNullException.ThrowIfNull(pages);

        var snapshotPages = pages
            .OrderBy(page => page.Position)
            .ThenBy(page => page.Slug, StringComparer.Ordinal)
            .Select(page => new SiteSnapshotPage(
                page.Slug,
                page.PageType.ToString(),
                page.Title,
                page.ShowInNav,
                page.Position,
                page.MetaTitle,
                page.MetaDescription,
                page.Blocks
                    .OrderBy(block => block.Position)
                    .Select(block => SiteBlocks.FromStored(block.BlockType, block.Config))
                    .OfType<SiteBlockDto>()
                    .ToList()))
            .ToList();

        return new SiteContentSnapshot(
            SchemaVersion,
            new SiteSnapshotSettings(site.Name, site.Language, site.SeoTitle, site.SeoDescription, site.FlightSearchEnabled),
            BusinessOf(branding),
            snapshotPages);
    }

    /// <summary>The site's look: the template's layout and the agency's logo and colours.</summary>
    public static SiteThemeSnapshot BuildTheme(string templateCode, AgencyBranding branding, SiteTheme? theme)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateCode);
        ArgumentNullException.ThrowIfNull(branding);

        return new SiteThemeSnapshot(
            SchemaVersion,
            templateCode,
            branding.LogoAssetId,
            branding.PrimaryColor,
            branding.SecondaryColor,
            theme?.HeadingFont ?? SiteFonts.DefaultHeading,
            SiteFonts.Body);
    }

    /// <summary>How travellers reach the agency, from its branding.</summary>
    public static SiteSnapshotBusiness BusinessOf(AgencyBranding branding)
    {
        ArgumentNullException.ThrowIfNull(branding);

        return new SiteSnapshotBusiness(
            branding.ContactAddress,
            branding.ContactEmail,
            branding.ContactPhone,
            branding.WhatsAppNumber,
            SocialLinksOf(branding));
    }

    /// <summary>The agency's social links that are safe to show, in footer order.</summary>
    public static IReadOnlyList<SiteSocialLinkDto> SocialLinksOf(AgencyBranding branding)
    {
        ArgumentNullException.ThrowIfNull(branding);

        Dictionary<string, string>? stored;

        try
        {
            stored = JsonSerializer.Deserialize<Dictionary<string, string>>(branding.SocialLinks);
        }
        catch (JsonException)
        {
            stored = null;
        }

        if (stored is null)
        {
            return [];
        }

        return SocialNetworks
            .Where(network => stored.TryGetValue(network, out var url) && IsSafeProfileUrl(url))
            .Select(network => new SiteSocialLinkDto(network, stored[network]))
            .ToList();
    }

    /// <summary>An <c>https://</c> address with a host and no username: all a social profile needs.</summary>
    public static bool IsSafeProfileUrl(string? url) =>
        url is { Length: > 0 and <= 300 }
        && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(uri.UserInfo)
        && !string.IsNullOrEmpty(uri.Host);

    public static string Serialize<T>(T snapshot) => JsonSerializer.Serialize(snapshot, SiteBlocks.Json);

    /// <summary>The content of a stored snapshot, or null when it cannot be read.</summary>
    public static SiteContentSnapshot? ReadContent(string? json) => Read<SiteContentSnapshot>(json);

    /// <summary>The theme of a stored snapshot, or null when it cannot be read.</summary>
    public static SiteThemeSnapshot? ReadTheme(string? json) => Read<SiteThemeSnapshot>(json);

    /// <summary>One spelling of a snapshot, for comparing two of them.</summary>
    public static string Canonical<T>(T snapshot) => Serialize(snapshot);

    private static T? Read<T>(string? json)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, SiteBlocks.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
