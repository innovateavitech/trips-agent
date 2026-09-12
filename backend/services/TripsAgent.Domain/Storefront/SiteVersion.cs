using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Storefront;

/// <summary>
/// One version of a site: the editable draft, or a frozen snapshot of it.
/// </summary>
/// <remarks>
/// <para>
/// The draft (number 0) holds the site's pages and blocks as rows, because that is what the builder
/// edits. Staging freezes them into <see cref="ContentSnapshot"/> and <see cref="ThemeSnapshot"/> —
/// versioned JSON the storefront renders from and never has to query around. From then on the
/// version cannot change: a database trigger refuses any edit to the snapshot of a staged, published
/// or archived version.
/// </para>
/// <para>
/// So a rollback is a pointer move, not a copy, and a publish can never half-apply.
/// </para>
/// </remarks>
public sealed class SiteVersion : Entity, IAuditableEntity, ITenantScoped
{
    /// <summary>The draft's number. Staged versions count up from 1.</summary>
    public const int DraftNumber = 0;

    private SiteVersion()
    {
    }

    /// <summary>The working copy for a newly created site.</summary>
    public static SiteVersion CreateDraft(Site site)
    {
        ArgumentNullException.ThrowIfNull(site);

        return new SiteVersion
        {
            AgencyId = site.AgencyId,
            SiteId = site.Id,
            VersionNumber = DraftNumber,
            Status = SiteVersionStatus.Draft,
        };
    }

    /// <summary>A frozen copy of the draft, as it is right now.</summary>
    /// <param name="site">The site it belongs to.</param>
    /// <param name="versionNumber">The next number for this site, from 1.</param>
    /// <param name="contentSnapshot">Pages, blocks and settings, as versioned JSON.</param>
    /// <param name="themeSnapshot">Logo, colours and typography, as versioned JSON.</param>
    /// <param name="userId">Who staged it.</param>
    /// <param name="now">When.</param>
    public static SiteVersion Stage(
        Site site,
        int versionNumber,
        string contentSnapshot,
        string themeSnapshot,
        Guid? userId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(site);
        ArgumentOutOfRangeException.ThrowIfLessThan(versionNumber, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentSnapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(themeSnapshot);

        return new SiteVersion
        {
            AgencyId = site.AgencyId,
            SiteId = site.Id,
            VersionNumber = versionNumber,
            Status = SiteVersionStatus.Staged,
            ContentSnapshot = contentSnapshot,
            ThemeSnapshot = themeSnapshot,
            StagedAt = now,
            StagedByUserId = userId,
        };
    }

    public Guid AgencyId { get; private set; }

    public Guid SiteId { get; private set; }

    /// <summary>0 for the draft; 1, 2, 3 … for each staging, in order.</summary>
    public int VersionNumber { get; private set; }

    public SiteVersionStatus Status { get; private set; }

    /// <summary>Null for the draft, whose content is its page rows.</summary>
    public string? ContentSnapshot { get; private set; }

    /// <summary>Null for the draft, whose theme is read live from branding.</summary>
    public string? ThemeSnapshot { get; private set; }

    public DateTimeOffset? StagedAt { get; private set; }

    public Guid? StagedByUserId { get; private set; }

    /// <summary>The last time this version went live — by publishing or by a rollback to it.</summary>
    public DateTimeOffset? PublishedAt { get; private set; }

    public Guid? PublishedByUserId { get; private set; }

    public DateTimeOffset? ArchivedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>True for a version that has been live before and could be put back.</summary>
    public bool CanBeRolledBackTo => Status == SiteVersionStatus.Archived && PublishedAt is not null;

    /// <summary>
    /// Retires a staged version that a newer staging replaced. It was never live, so it cannot be
    /// rolled back to.
    /// </summary>
    public void Supersede(DateTimeOffset now)
    {
        if (Status != SiteVersionStatus.Staged)
        {
            throw new InvalidOperationException("Only a staged version can be superseded.");
        }

        Status = SiteVersionStatus.Archived;
        ArchivedAt = now;
    }

    /// <summary>Goes live. Only <see cref="Site"/> calls this, so the site's pointer moves with it.</summary>
    internal void MarkPublished(Guid? userId, DateTimeOffset now)
    {
        if (Status != SiteVersionStatus.Staged && !CanBeRolledBackTo)
        {
            throw new InvalidOperationException($"A {Status} version cannot go live.");
        }

        Status = SiteVersionStatus.Published;
        PublishedAt = now;
        PublishedByUserId = userId;
        ArchivedAt = null;
    }

    /// <summary>Stops being live. Keeps its snapshot, so it can come back.</summary>
    internal void Archive(DateTimeOffset now)
    {
        if (Status != SiteVersionStatus.Published)
        {
            throw new InvalidOperationException($"Only the live version is archived when another replaces it, not a {Status} one.");
        }

        Status = SiteVersionStatus.Archived;
        ArchivedAt = now;
    }
}
