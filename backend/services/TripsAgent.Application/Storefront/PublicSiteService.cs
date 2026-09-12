using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Assets;
using TripsAgent.Application.Persistence;
using TripsAgent.Contracts.Storefront;
using TripsAgent.Domain.Assets;
using TripsAgent.Domain.Storefront;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.Application.Storefront;

/// <summary>
/// What the traveller-facing site shows on one hostname: the published version's pages, the agency's
/// look, and nothing else (issue 60).
/// </summary>
/// <remarks>
/// <para>
/// Runs after <see cref="PublicSiteResolver"/> has put the request inside an agency, so every read
/// here is an ordinary tenant-filtered read.
/// </para>
/// <para>
/// <b>Nothing it returns mentions the platform</b> (CLAUDE.md rule 4). The name, logo, colours and
/// contact details are the agency's, taken from its branding and frozen into the version it
/// published. A site that is not live answers in that same branding rather than in ours.
/// </para>
/// </remarks>
public sealed class PublicSiteService
{
    /// <summary>A logo is shown at its own size in a header, so the medium rendition is plenty.</summary>
    private static readonly AssetVariantKind[] LogoPreference =
        [AssetVariantKind.Medium, AssetVariantKind.Original, AssetVariantKind.Large, AssetVariantKind.Thumbnail];

    private readonly IAppDbContext _db;
    private readonly AssetDelivery _delivery;
    private readonly SitePreviewTokens _previewTokens;
    private readonly StorefrontOptions _options;

    public PublicSiteService(
        IAppDbContext db,
        AssetDelivery delivery,
        SitePreviewTokens previewTokens,
        StorefrontOptions options)
    {
        _db = db;
        _delivery = delivery;
        _previewTokens = previewTokens;
        _options = options;
    }

    /// <summary>
    /// The site on <paramref name="route"/>'s hostname, ready to render.
    /// </summary>
    /// <param name="previewToken">
    /// A signed preview link's token, when the caller has one. It shows an unpublished version of
    /// <b>this same site</b> and nothing else: a token for another agency's site is ignored, not
    /// honoured, so a leaked link cannot be replayed against a stranger's hostname.
    /// </param>
    public async Task<PublicSiteResponse?> GetAsync(
        StorefrontHostRoute route,
        string? previewToken = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);

        var site = await _db.Sites.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == route.SiteId, cancellationToken);

        if (site is null)
        {
            return null;
        }

        var agencyStatus = await _db.Agencies.AsNoTracking()
            .Where(agency => agency.Id == route.AgencyId)
            .Select(agency => agency.Status)
            .FirstOrDefaultAsync(cancellationToken);

        var branding = await _db.AgencyBranding.AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);

        var grant = _previewTokens.Read(previewToken);
        var previewing = grant is not null && grant.SiteId == site.Id && grant.AgencyId == route.AgencyId;

        var version = previewing
            ? await VersionAsync(grant!.VersionId, cancellationToken)
            : site.PublishedVersionId is { } publishedId
                ? await VersionAsync(publishedId, cancellationToken)
                : null;

        var status = previewing && version is not null
            ? PublicSiteStatuses.Preview
            : agencyStatus is AgencyStatus.Suspended or AgencyStatus.Terminated
                ? PublicSiteStatuses.Offline
                : version is null
                    ? PublicSiteStatuses.OpeningSoon
                    : PublicSiteStatuses.Live;

        var content = status is PublicSiteStatuses.Live or PublicSiteStatuses.Preview
            ? SiteSnapshots.ReadContent(version?.ContentSnapshot)
            : null;

        var theme = SiteSnapshots.ReadTheme(version?.ThemeSnapshot);

        return new PublicSiteResponse(
            status,
            site.Id,
            route.Hostname,
            route.PrimaryHostname,
            route.IsPrimaryHostname,
            // Only the live site on its main address is worth indexing. A preview, a shut shop and a
            // second hostname all point somewhere else or show nothing worth finding in a search.
            Indexable: status == PublicSiteStatuses.Live && route.IsPrimaryHostname,
            version?.VersionNumber,
            version?.PublishedAt,
            content ?? Closed(site, branding),
            theme ?? await FallbackThemeAsync(site, branding, cancellationToken));
    }

    /// <summary>The site's canonical address, for canonical links and the sitemap.</summary>
    public string BaseUrlFor(StorefrontHostRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);

        return _options.SiteUrlFor(route.PrimaryHostname);
    }

    /// <summary>A signed link to the agency's logo, or null when it has none that may be shown.</summary>
    public async Task<string?> LogoUrlAsync(Guid? logoAssetId, CancellationToken cancellationToken = default)
    {
        if (logoAssetId is not { } assetId)
        {
            return null;
        }

        var asset = await _db.Assets.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == assetId, cancellationToken);

        if (asset is null)
        {
            return null;
        }

        var variants = await _db.AssetVariants.AsNoTracking()
            .Where(variant => variant.AssetId == assetId)
            .ToListAsync(cancellationToken);

        var links = await _delivery.LinksForAsync(asset, variants, cancellationToken);

        return LogoPreference
            .Select(kind => links.FirstOrDefault(link => link.Kind == kind))
            .FirstOrDefault(link => link is not null)
            ?.Url;
    }

    private Task<SiteVersion?> VersionAsync(Guid versionId, CancellationToken cancellationToken) =>
        _db.SiteVersions.AsNoTracking()
            .FirstOrDefaultAsync(version => version.Id == versionId, cancellationToken);

    /// <summary>
    /// What a site shows when there is nothing published to show: its name and how to reach it, in
    /// its own branding. Never a platform page.
    /// </summary>
    private static SiteContentSnapshot Closed(Site site, AgencyBranding? branding) =>
        new(
            SiteSnapshots.SchemaVersion,
            new SiteSnapshotSettings(site.Name, site.Language, site.SeoTitle, site.SeoDescription, FlightSearchEnabled: false),
            branding is null
                ? new SiteSnapshotBusiness(null, null, null, null, [])
                : SiteSnapshots.BusinessOf(branding),
            []);

    /// <summary>
    /// A look for a site with no published version: the agency's own colours from its branding, and
    /// the template it chose. Not a default palette — a shop that is not open yet is still theirs.
    /// </summary>
    private async Task<SiteThemeSnapshot> FallbackThemeAsync(
        Site site,
        AgencyBranding? branding,
        CancellationToken cancellationToken)
    {
        var templateCode = await _db.SiteTemplates.AsNoTracking()
            .Where(template => template.Id == site.TemplateId)
            .Select(template => template.Code)
            .FirstOrDefaultAsync(cancellationToken);

        var theme = await _db.SiteThemes.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.SiteId == site.Id, cancellationToken);

        return branding is null
            ? new SiteThemeSnapshot(
                SiteSnapshots.SchemaVersion,
                templateCode ?? SiteTemplateCatalog.DefaultCode,
                null,
                AgencyBranding.DefaultPrimaryColor,
                null,
                theme?.HeadingFont ?? SiteFonts.DefaultHeading,
                SiteFonts.Body)
            : SiteSnapshots.BuildTheme(templateCode ?? SiteTemplateCatalog.DefaultCode, branding, theme);
    }
}
