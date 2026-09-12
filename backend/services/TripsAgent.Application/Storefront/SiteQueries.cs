using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Contracts.Storefront;
using TripsAgent.Domain.Assets;
using TripsAgent.Domain.Catalog;
using TripsAgent.Domain.Storefront;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.Application.Storefront;

/// <summary>
/// The reads the website builder's use cases share: the draft, the branding, the publish gate's facts.
/// </summary>
/// <remarks>
/// Every query here runs under the tenant filter. Nothing in the builder reads across agencies except
/// the free-subdomain search, which says why in its own platform scope.
/// </remarks>
public sealed class SiteQueries
{
    private readonly IAppDbContext _db;
    private readonly ITenantContext _tenant;

    public SiteQueries(IAppDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    /// <summary>The agency this request acts for. Every builder endpoint requires one.</summary>
    public Guid AgencyId => _tenant.AgencyId ?? throw new InvalidOperationException(
        "The website builder works for an agency, and none is resolved for this request.");

    /// <summary>The draft's pages with their blocks, in navigation order.</summary>
    public async Task<List<SitePage>> DraftPagesAsync(Site site, bool tracked, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(site);

        var query = _db.SitePages.Include(page => page.Blocks).Where(page => page.VersionId == site.DraftVersionId);

        if (!tracked)
        {
            query = query.AsNoTracking();
        }

        return await query.OrderBy(page => page.Position).ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The agency's branding. An agency registered before branding existed gets neutral defaults: added
    /// to the save when <paramref name="tracked"/>, or only in memory for a read.
    /// </summary>
    public async Task<AgencyBranding> BrandingAsync(bool tracked, CancellationToken cancellationToken)
    {
        var query = tracked ? _db.AgencyBranding : _db.AgencyBranding.AsNoTracking();
        var branding = await query.FirstOrDefaultAsync(cancellationToken);

        if (branding is not null)
        {
            return branding;
        }

        var agency = await _db.Agencies.AsNoTracking().FirstAsync(candidate => candidate.Id == AgencyId, cancellationToken);
        branding = AgencyBranding.CreateDefault(agency);

        if (tracked)
        {
            _db.AgencyBranding.Add(branding);
        }

        return branding;
    }

    /// <summary>The draft, frozen the way staging would freeze it right now.</summary>
    public async Task<(SiteContentSnapshot Content, SiteThemeSnapshot Theme)> DraftSnapshotsAsync(
        Site site,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(site);

        var templateCode = await _db.SiteTemplates.AsNoTracking()
            .Where(template => template.Id == site.TemplateId)
            .Select(template => template.Code)
            .FirstAsync(cancellationToken);

        var pages = await DraftPagesAsync(site, tracked: false, cancellationToken);
        var branding = await BrandingAsync(tracked: false, cancellationToken);
        var theme = await _db.SiteThemes.AsNoTracking().FirstOrDefaultAsync(candidate => candidate.SiteId == site.Id, cancellationToken);

        return (SiteSnapshots.BuildContent(site, branding, pages), SiteSnapshots.BuildTheme(templateCode, branding, theme));
    }

    /// <summary>What the publish gate needs to decide about this site.</summary>
    public async Task<PublishGateFacts> GateFactsAsync(Site site, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(site);

        var agencyStatus = await _db.Agencies.AsNoTracking()
            .Where(agency => agency.Id == AgencyId)
            .Select(agency => agency.Status)
            .FirstOrDefaultAsync(cancellationToken);

        var publishedProducts = await _db.Products.CountAsync(product => product.Status == ProductStatus.Published, cancellationToken);

        return new PublishGateFacts(agencyStatus == AgencyStatus.Verified, publishedProducts, site.FlightSearchEnabled);
    }

    /// <summary>Names for the people who staged and published, keyed by user id.</summary>
    public async Task<IReadOnlyDictionary<Guid, string>> UserNamesAsync(IEnumerable<Guid?> userIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userIds);

        var ids = userIds.OfType<Guid>().Distinct().ToList();

        if (ids.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var users = await _db.Users.AsNoTracking()
            .Where(user => ids.Contains(user.Id))
            .Select(user => new { user.Id, user.FirstName, user.LastName })
            .ToListAsync(cancellationToken);

        return users.ToDictionary(user => user.Id, user => $"{user.FirstName} {user.LastName}".Trim());
    }

    /// <summary>
    /// Why a snapshot's images could not all be shown: one sentence per image that is missing or not yet
    /// scanned clean. Empty when every image is ready.
    /// </summary>
    public async Task<IReadOnlyList<string>> UnreadyImagesAsync(
        SiteContentSnapshot content,
        SiteThemeSnapshot theme,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(theme);

        var ids = content.Pages
            .SelectMany(page => page.Blocks)
            .Select(block => block.Hero?.ImageAssetId)
            .Append(theme.LogoAssetId)
            .OfType<Guid>()
            .Distinct()
            .ToList();

        if (ids.Count == 0)
        {
            return [];
        }

        var assets = await _db.Assets.AsNoTracking().Where(asset => ids.Contains(asset.Id)).ToListAsync(cancellationToken);

        return ids
            .Select(id => assets.FirstOrDefault(asset => asset.Id == id))
            .Select((asset, index) => asset switch
            {
                null => "An image on your site is no longer in your uploads. Choose another.",
                { IsServable: false } => $"The image {asset.FileName} is still being checked. Try again in a minute.",
                _ => null,
            })
            .OfType<string>()
            .ToList();
    }

    /// <summary>Checks every image a page save points at, adding an error beside each field that fails.</summary>
    public async Task CheckImagesAsync(IReadOnlyList<(Guid AssetId, string Field)> images, FieldErrors errors, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(errors);

        if (images.Count == 0)
        {
            return;
        }

        var ids = images.Select(image => image.AssetId).Distinct().ToList();
        var assets = await _db.Assets.AsNoTracking().Where(asset => ids.Contains(asset.Id)).ToListAsync(cancellationToken);

        foreach (var (assetId, field) in images)
        {
            var asset = assets.FirstOrDefault(candidate => candidate.Id == assetId);

            if (asset is null)
            {
                errors.Add(field, "That image is not one of your uploads.");
            }
            else if (!asset.IsServable)
            {
                errors.Add(field, "That image is still being checked. Try again in a minute.");
            }
            else if (!asset.IsImage || asset.Purpose is not (AssetPurpose.SiteMedia or AssetPurpose.AgencyLogo or AssetPurpose.ProductMedia))
            {
                errors.Add(field, "Choose a photo uploaded for your website.");
            }
        }
    }

    /// <summary>Checks every product a grid names is in this agency's catalog.</summary>
    public async Task CheckProductsAsync(IReadOnlyList<(Guid ProductId, string Field)> products, FieldErrors errors, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(products);
        ArgumentNullException.ThrowIfNull(errors);

        if (products.Count == 0)
        {
            return;
        }

        var ids = products.Select(product => product.ProductId).Distinct().ToList();
        var known = await _db.Products.AsNoTracking().Where(product => ids.Contains(product.Id)).Select(product => product.Id).ToListAsync(cancellationToken);

        foreach (var (productId, field) in products.Where(product => !known.Contains(product.ProductId)))
        {
            errors.Add(field, $"Product {productId} is not in your catalog.");
        }
    }

    /// <summary>A version as the publishing panel shows it.</summary>
    public static SiteVersionResponse ToResponse(
        Guid id,
        int number,
        SiteVersionStatus status,
        DateTimeOffset? stagedAt,
        Guid? stagedBy,
        DateTimeOffset? publishedAt,
        Guid? publishedBy,
        Guid? liveVersionId,
        IReadOnlyDictionary<Guid, string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        return new SiteVersionResponse(
            id,
            number,
            status.ToString(),
            stagedAt,
            stagedBy is { } staged && names.TryGetValue(staged, out var stagedName) ? stagedName : null,
            publishedAt,
            publishedBy is { } published && names.TryGetValue(published, out var publishedName) ? publishedName : null,
            status == SiteVersionStatus.Archived && publishedAt is not null,
            liveVersionId == id);
    }

    /// <summary>The version history line for an entity already loaded.</summary>
    public static SiteVersionResponse ToResponse(SiteVersion version, Site site, IReadOnlyDictionary<Guid, string> names)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(site);

        return ToResponse(
            version.Id,
            version.VersionNumber,
            version.Status,
            version.StagedAt,
            version.StagedByUserId,
            version.PublishedAt,
            version.PublishedByUserId,
            site.PublishedVersionId,
            names);
    }
}
