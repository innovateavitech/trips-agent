using TripsAgent.Domain.Auditing;
using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Storefront;

/// <summary>
/// An agency's website: its settings, and which of its versions is the draft and which is live.
/// </summary>
/// <remarks>
/// <para>
/// One per agency — <c>agency_id</c> is UNIQUE (plan §2.4). The content itself lives in
/// <see cref="SiteVersion"/>s: every edit goes to the draft, staging freezes a copy of it, and
/// publishing points <see cref="PublishedVersionId"/> at that copy. Rolling back points it at an
/// older copy. Nothing is ever copied back, which is what makes a rollback instant and safe.
/// </para>
/// <para>
/// Audited: a publish or a rollback changes <see cref="PublishedVersionId"/>, so the audit log
/// records who moved the site from which version to which, with no extra code.
/// </para>
/// </remarks>
public sealed class Site : AggregateRoot, IAuditableEntity, ITenantScoped, IAuditLogged
{
    public const int MaxNameLength = 120;

    /// <summary>What search engines show of a title. Longer is cut off mid-word.</summary>
    public const int MaxSeoTitleLength = 70;

    /// <summary>What search engines show of a description.</summary>
    public const int MaxSeoDescriptionLength = 160;

    /// <summary>English only in M2; the column exists so a translated site needs no migration.</summary>
    public const string DefaultLanguage = "en";

    private Site()
    {
        Name = string.Empty;
        Language = DefaultLanguage;
        AnalyticsIds = "{}";
    }

    /// <summary>Creates an agency's site. Its draft and its free address are attached next.</summary>
    public static Site Create(Guid agencyId, Guid templateId, string name)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(templateId, Guid.Empty);

        var site = new Site
        {
            AgencyId = agencyId,
            TemplateId = templateId,
            Status = SiteStatus.Draft,
            Name = RequireName(name),
            Language = DefaultLanguage,
        };

        site.Raise(new SiteCreated(site.Id, agencyId));

        return site;
    }

    public Guid AgencyId { get; private set; }

    /// <summary>The template the site was started from. It decides the layout the storefront uses.</summary>
    public Guid TemplateId { get; private set; }

    public SiteStatus Status { get; private set; }

    /// <summary>The name travellers see — in the header, the title bar and every search result.</summary>
    public string Name { get; private set; }

    public string? SeoTitle { get; private set; }

    public string? SeoDescription { get; private set; }

    /// <summary>The site's language, as <c>lang</c> on every page.</summary>
    public string Language { get; private set; }

    /// <summary>
    /// The agency sells flights through its site. One half of the publish gate's rule for open
    /// question 11 — see <see cref="SomethingToSellRule"/>.
    /// </summary>
    public bool FlightSearchEnabled { get; private set; }

    /// <summary>
    /// The agency's own analytics tags. Kept from the plan, but nothing writes it yet: injecting
    /// trackers needs a consent decision nobody has made (the storefront epic's finding 5).
    /// </summary>
    public string AnalyticsIds { get; private set; }

    /// <summary>The working copy. Set once, when the site is created, and never changed.</summary>
    public Guid? DraftVersionId { get; private set; }

    /// <summary>What travellers see. Null until the first publish.</summary>
    public Guid? PublishedVersionId { get; private set; }

    /// <summary>
    /// The hostname canonical URLs point at. Authoritative: <c>site_domains</c> has no
    /// <c>is_primary</c> of its own, so the two can never disagree (the domains epic's finding 4).
    /// </summary>
    public Guid? PrimaryDomainId { get; private set; }

    /// <summary>Bumped by every change, and checked by the database on every save.</summary>
    public int Version { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Attaches the draft every edit will go to. Done once, when the site is created.</summary>
    public void AttachDraft(SiteVersion draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        if (DraftVersionId is not null)
        {
            throw new InvalidOperationException("This site already has its draft.");
        }

        if (draft.SiteId != Id || draft.Status != SiteVersionStatus.Draft)
        {
            throw new ArgumentException("The draft must be this site's own, and a draft.", nameof(draft));
        }

        DraftVersionId = draft.Id;
    }

    /// <summary>Changes what the site is called and how it presents itself to search engines.</summary>
    public void UpdateSettings(string name, string? seoTitle, string? seoDescription, bool flightSearchEnabled)
    {
        Name = RequireName(name);
        SeoTitle = Optional(seoTitle, MaxSeoTitleLength, nameof(seoTitle));
        SeoDescription = Optional(seoDescription, MaxSeoDescriptionLength, nameof(seoDescription));
        FlightSearchEnabled = flightSearchEnabled;
        Version++;
    }

    /// <summary>Makes <paramref name="domain"/> the address canonical URLs point at.</summary>
    /// <remarks>
    /// A custom hostname must already serve the site over HTTPS: verified, with a certificate, and not set
    /// aside for review. A canonical URL pointing at a host that shows a browser warning would take the
    /// site's search ranking down with it. The free subdomain is always allowed — it is the fallback every
    /// site has, and one waiting for review simply serves nothing until it is cleared.
    /// </remarks>
    public void SetPrimaryDomain(SiteDomain domain)
    {
        ArgumentNullException.ThrowIfNull(domain);

        if (domain.SiteId != Id)
        {
            throw new ArgumentException("That hostname belongs to another site.", nameof(domain));
        }

        if (domain.Type != SiteDomainType.Subdomain && !domain.CanServeSecurely)
        {
            throw new InvalidOperationException(
                "Only a verified hostname with a certificate can be the site's main address.");
        }

        PrimaryDomainId = domain.Id;
        Version++;
    }

    /// <summary>Puts <paramref name="staged"/> live, archiving whatever was live before.</summary>
    /// <param name="staged">The version to publish. Must be this site's staged version.</param>
    /// <param name="current">The version live now, or null for a first publish.</param>
    /// <param name="userId">Who published, for the version history.</param>
    /// <param name="now">When.</param>
    public void Publish(SiteVersion staged, SiteVersion? current, Guid? userId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(staged);

        if (staged.SiteId != Id || staged.Status != SiteVersionStatus.Staged)
        {
            throw new InvalidOperationException("Only this site's staged version can be published.");
        }

        var previous = ReplaceLiveVersion(current, now);

        staged.MarkPublished(userId, now);
        PublishedVersionId = staged.Id;
        Status = SiteStatus.Published;
        Version++;

        Raise(new SitePublished(Id, AgencyId, staged.Id, staged.VersionNumber, previous, IsRollback: false));
    }

    /// <summary>Puts an earlier published version back live. Nothing is copied.</summary>
    /// <remarks>
    /// Never gated. A site must always be able to go back to a version that has been live before,
    /// whatever has changed since — that is the whole point of keeping them.
    /// </remarks>
    public void RollBackTo(SiteVersion target, SiteVersion current, Guid? userId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(current);

        if (target.SiteId != Id || !target.CanBeRolledBackTo)
        {
            throw new InvalidOperationException("Only a version of this site that has been live before can be put back.");
        }

        var previous = ReplaceLiveVersion(current, now);

        target.MarkPublished(userId, now);
        PublishedVersionId = target.Id;
        Version++;

        Raise(new SitePublished(Id, AgencyId, target.Id, target.VersionNumber, previous, IsRollback: true));
    }

    /// <summary>Records that the draft was frozen into a new staged version.</summary>
    public void RecordStaged() => Version++;

    private Guid? ReplaceLiveVersion(SiteVersion? current, DateTimeOffset now)
    {
        if (PublishedVersionId is null)
        {
            if (current is not null)
            {
                throw new ArgumentException("Nothing is live yet, so there is no current version.", nameof(current));
            }

            return null;
        }

        if (current is null || current.Id != PublishedVersionId)
        {
            throw new ArgumentException("The current version must be the one that is live.", nameof(current));
        }

        current.Archive(now);
        return current.Id;
    }

    private static string RequireName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A site needs a name.", nameof(name));
        }

        var trimmed = name.Trim();

        return trimmed.Length <= MaxNameLength
            ? trimmed
            : throw new ArgumentException($"A site name can be at most {MaxNameLength} characters.", nameof(name));
    }

    private static string? Optional(string? value, int maxLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        return trimmed.Length <= maxLength
            ? trimmed
            : throw new ArgumentException($"That can be at most {maxLength} characters.", parameterName);
    }
}
