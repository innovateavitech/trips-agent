using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Auditing;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Contracts.Storefront;
using TripsAgent.Domain.Storefront;

namespace TripsAgent.Application.Storefront;

/// <summary>
/// Staging, publishing and rolling back a site, and the preview links that show a version before it
/// goes live.
/// </summary>
/// <remarks>
/// <para>
/// Each change runs in one transaction under the row lock on the agency's site, so two at once queue
/// rather than interleave: of two publishes racing, exactly one wins and the other finds nothing
/// staged. The database backs that up — at most one staged and one live version per site.
/// </para>
/// <para>
/// A publish or rollback moves <see cref="Site.PublishedVersionId"/>, which the audit log records with
/// who did it, and raises <see cref="SitePublished"/> through the outbox for the storefront's cache.
/// </para>
/// </remarks>
public sealed class SiteVersionService
{
    /// <summary>How many versions the history shows.</summary>
    public const int HistoryLength = 20;

    private readonly IAppDbContext _db;
    private readonly SiteQueries _queries;
    private readonly ITransactionRunner _transactions;
    private readonly ISiteLock _siteLock;
    private readonly IAuditContext _audit;
    private readonly ITenantContext _tenant;
    private readonly SitePreviewTokens _previewTokens;
    private readonly StorefrontOptions _options;
    private readonly TimeProvider _clock;

    public SiteVersionService(
        IAppDbContext db,
        SiteQueries queries,
        ITransactionRunner transactions,
        ISiteLock siteLock,
        IAuditContext audit,
        ITenantContext tenant,
        SitePreviewTokens previewTokens,
        StorefrontOptions options,
        TimeProvider clock)
    {
        _db = db;
        _queries = queries;
        _transactions = transactions;
        _siteLock = siteLock;
        _audit = audit;
        _tenant = tenant;
        _previewTokens = previewTokens;
        _options = options;
        _clock = clock;
    }

    /// <summary>
    /// Freezes the draft into a new staged version, ready to preview and publish. The agent can keep
    /// editing the draft afterwards without changing what is staged.
    /// </summary>
    public Task<StorefrontResult<SiteVersionResponse>> StageAsync(CancellationToken cancellationToken = default) =>
        _transactions.RunAsync<StorefrontResult<SiteVersionResponse>>(
            async token =>
            {
                var site = await LockedSiteAsync(token);

                if (site is null)
                {
                    return new StorefrontResult<SiteVersionResponse>.NotFound("You have not created a website yet.");
                }

                var (content, theme) = await _queries.DraftSnapshotsAsync(site, token);
                var unready = await _queries.UnreadyImagesAsync(content, theme, token);

                if (unready.Count > 0)
                {
                    return new StorefrontResult<SiteVersionResponse>.Conflict("Some images are not ready yet.", string.Join(" ", unready));
                }

                var now = _clock.GetUtcNow();
                var staged = await _db.SiteVersions.FirstOrDefaultAsync(
                    version => version.SiteId == site.Id && version.Status == SiteVersionStatus.Staged,
                    token);

                // Nothing changed since the last staging: that version is still the right preview.
                if (staged is not null && SameContent(staged, content, theme))
                {
                    return new StorefrontResult<SiteVersionResponse>.Ok(await ResponseAsync(staged, site, token));
                }

                staged?.Supersede(now);

                var next = (await _db.SiteVersions
                    .Where(version => version.SiteId == site.Id)
                    .MaxAsync(version => (int?)version.VersionNumber, token) ?? 0) + 1;

                var frozen = SiteVersion.Stage(
                    site,
                    next,
                    SiteSnapshots.Serialize(content),
                    SiteSnapshots.Serialize(theme),
                    _tenant.UserId,
                    now);

                _db.SiteVersions.Add(frozen);
                site.RecordStaged();

                await _db.SaveChangesAsync(token);

                return new StorefrontResult<SiteVersionResponse>.Ok(await ResponseAsync(frozen, site, token));
            },
            cancellationToken);

    /// <summary>
    /// Puts the staged version live. Refused, with every reason, when the publish gate says the site is
    /// not ready — see <see cref="SitePublishGate"/>.
    /// </summary>
    public Task<StorefrontResult<SiteVersionResponse>> PublishAsync(Guid versionId, CancellationToken cancellationToken = default) =>
        _transactions.RunAsync<StorefrontResult<SiteVersionResponse>>(
            async token =>
            {
                var site = await LockedSiteAsync(token);

                if (site is null)
                {
                    return new StorefrontResult<SiteVersionResponse>.NotFound("You have not created a website yet.");
                }

                var version = await _db.SiteVersions.FirstOrDefaultAsync(
                    candidate => candidate.Id == versionId && candidate.SiteId == site.Id,
                    token);

                if (version is null)
                {
                    return new StorefrontResult<SiteVersionResponse>.NotFound("There is no version with that id.");
                }

                if (version.Status != SiteVersionStatus.Staged)
                {
                    return new StorefrontResult<SiteVersionResponse>.Conflict(
                        "That version cannot be published.",
                        "Only the staged version can be published. Stage your latest changes, then publish them.");
                }

                var problems = SitePublishGate.Check(await _queries.GateFactsAsync(site, token));

                if (problems.Count > 0)
                {
                    return new StorefrontResult<SiteVersionResponse>.Refused(
                        "Your site cannot be published yet.",
                        string.Join(" ", problems.Select(problem => problem.Message)),
                        problems);
                }

                var current = await LiveVersionAsync(site, token);
                site.Publish(version, current, _tenant.UserId, _clock.GetUtcNow());

                _audit.SetReason(current is null
                    ? $"Published version {version.VersionNumber}"
                    : $"Published version {version.VersionNumber}, replacing version {current.VersionNumber}");

                await _db.SaveChangesAsync(token);

                return new StorefrontResult<SiteVersionResponse>.Ok(await ResponseAsync(version, site, token));
            },
            cancellationToken);

    /// <summary>Puts an earlier live version back. Never gated: going back must always work.</summary>
    public Task<StorefrontResult<SiteVersionResponse>> RollBackAsync(Guid versionId, CancellationToken cancellationToken = default) =>
        _transactions.RunAsync<StorefrontResult<SiteVersionResponse>>(
            async token =>
            {
                var site = await LockedSiteAsync(token);

                if (site is null)
                {
                    return new StorefrontResult<SiteVersionResponse>.NotFound("You have not created a website yet.");
                }

                var target = await _db.SiteVersions.FirstOrDefaultAsync(
                    candidate => candidate.Id == versionId && candidate.SiteId == site.Id,
                    token);

                if (target is null)
                {
                    return new StorefrontResult<SiteVersionResponse>.NotFound("There is no version with that id.");
                }

                var current = await LiveVersionAsync(site, token);

                if (current is null || !target.CanBeRolledBackTo)
                {
                    return new StorefrontResult<SiteVersionResponse>.Conflict(
                        "That version cannot be put back.",
                        "Only a version that has been live before can replace the one that is live now.");
                }

                site.RollBackTo(target, current, _tenant.UserId, _clock.GetUtcNow());
                _audit.SetReason($"Rolled back to version {target.VersionNumber} from version {current.VersionNumber}");

                await _db.SaveChangesAsync(token);

                return new StorefrontResult<SiteVersionResponse>.Ok(await ResponseAsync(target, site, token));
            },
            cancellationToken);

    /// <summary>The latest versions, newest first, with who staged and published each.</summary>
    public async Task<StorefrontResult<IReadOnlyList<SiteVersionResponse>>> ListVersionsAsync(CancellationToken cancellationToken = default)
    {
        var site = await _db.Sites.AsNoTracking().FirstOrDefaultAsync(cancellationToken);

        if (site is null)
        {
            return new StorefrontResult<IReadOnlyList<SiteVersionResponse>>.NotFound("You have not created a website yet.");
        }

        // A projection, so the history does not drag every snapshot out of the database.
        var rows = await _db.SiteVersions.AsNoTracking()
            .Where(version => version.SiteId == site.Id && version.Status != SiteVersionStatus.Draft)
            .OrderByDescending(version => version.VersionNumber)
            .Take(HistoryLength)
            .Select(version => new
            {
                version.Id,
                version.VersionNumber,
                version.Status,
                version.StagedAt,
                version.StagedByUserId,
                version.PublishedAt,
                version.PublishedByUserId,
            })
            .ToListAsync(cancellationToken);

        var names = await _queries.UserNamesAsync(rows.SelectMany(row => new[] { row.StagedByUserId, row.PublishedByUserId }), cancellationToken);

        return new StorefrontResult<IReadOnlyList<SiteVersionResponse>>.Ok(rows
            .Select(row => SiteQueries.ToResponse(
                row.Id,
                row.VersionNumber,
                row.Status,
                row.StagedAt,
                row.StagedByUserId,
                row.PublishedAt,
                row.PublishedByUserId,
                site.PublishedVersionId,
                names))
            .ToList());
    }

    /// <summary>
    /// A short-lived link to one version — or to the draft as it is right now — on the site's own address.
    /// </summary>
    public async Task<StorefrontResult<SitePreviewLinkResponse>> PreviewLinkAsync(Guid? versionId, CancellationToken cancellationToken = default)
    {
        var site = await _db.Sites.AsNoTracking().FirstOrDefaultAsync(cancellationToken);

        if (site?.DraftVersionId is not { } draftId)
        {
            return new StorefrontResult<SitePreviewLinkResponse>.NotFound("You have not created a website yet.");
        }

        var targetId = versionId ?? draftId;

        if (versionId is not null && !await _db.SiteVersions.AnyAsync(version => version.Id == targetId && version.SiteId == site.Id, cancellationToken))
        {
            return new StorefrontResult<SitePreviewLinkResponse>.NotFound("There is no version with that id.");
        }

        var hostname = await _db.SiteDomains.AsNoTracking()
                           .Where(domain => domain.Id == site.PrimaryDomainId)
                           .Select(domain => domain.Hostname)
                           .FirstOrDefaultAsync(cancellationToken)
                       ?? await _db.SiteDomains.AsNoTracking()
                           .Where(domain => domain.SiteId == site.Id && domain.Type == SiteDomainType.Subdomain)
                           .Select(domain => domain.Hostname)
                           .FirstAsync(cancellationToken);

        var (token, expiresAt) = _previewTokens.Issue(site.AgencyId, site.Id, targetId);

        return new StorefrontResult<SitePreviewLinkResponse>.Ok(
            new SitePreviewLinkResponse($"{_options.SiteUrlFor(hostname)}/preview/{token}", expiresAt));
    }

    private async Task<Site?> LockedSiteAsync(CancellationToken cancellationToken)
    {
        var siteId = await _siteLock.LockCurrentSiteAsync(cancellationToken);

        return siteId is null ? null : await _db.Sites.FirstAsync(site => site.Id == siteId, cancellationToken);
    }

    private async Task<SiteVersion?> LiveVersionAsync(Site site, CancellationToken cancellationToken) =>
        site.PublishedVersionId is { } liveId
            ? await _db.SiteVersions.FirstAsync(version => version.Id == liveId, cancellationToken)
            : null;

    private async Task<SiteVersionResponse> ResponseAsync(SiteVersion version, Site site, CancellationToken cancellationToken)
    {
        var names = await _queries.UserNamesAsync([version.StagedByUserId, version.PublishedByUserId], cancellationToken);

        return SiteQueries.ToResponse(version, site, names);
    }

    private static bool SameContent(SiteVersion version, SiteContentSnapshot content, SiteThemeSnapshot theme) =>
        SiteSnapshots.ReadContent(version.ContentSnapshot) is { } stored
        && SiteSnapshots.ReadTheme(version.ThemeSnapshot) is { } storedTheme
        && string.Equals(SiteSnapshots.Canonical(stored), SiteSnapshots.Canonical(content), StringComparison.Ordinal)
        && string.Equals(SiteSnapshots.Canonical(storedTheme), SiteSnapshots.Canonical(theme), StringComparison.Ordinal);
}
