namespace TripsAgent.Domain.Storefront;

/// <summary>Whether an agency's website has ever gone live.</summary>
/// <remarks>
/// Derived from <see cref="Site.PublishedVersionId"/> and stored beside it so psql reads plainly. A
/// CHECK constraint keeps the two in step: a site is <see cref="Draft"/> exactly when nothing is
/// published.
/// </remarks>
public enum SiteStatus
{
    /// <summary>Built but never published. Travellers see the agency's "opening soon" page.</summary>
    Draft = 1,

    /// <summary>A version is live. Rolling back keeps the site live; it only changes which version.</summary>
    Published = 2,
}

/// <summary>Where one version of a site is in its life.</summary>
/// <remarks>
/// Only the draft can change. Everything else is a frozen snapshot, and a database trigger refuses
/// any edit to one — the same guarantee hard rule 5 gives an order line's price.
/// </remarks>
public enum SiteVersionStatus
{
    /// <summary>The working copy every edit goes to. Exactly one per site, numbered 0.</summary>
    Draft = 1,

    /// <summary>A frozen copy of the draft, waiting to be published. At most one per site.</summary>
    Staged = 2,

    /// <summary>What travellers see. At most one per site.</summary>
    Published = 3,

    /// <summary>Superseded. One that was ever published can be rolled back to.</summary>
    Archived = 4,
}

/// <summary>What a page is for. Every type but <see cref="Custom"/> is a system page.</summary>
public enum SitePageType
{
    Home = 1,
    About = 2,
    Contact = 3,
    Terms = 4,

    /// <summary>The listing of the agency's tours, packages and visas.</summary>
    Catalog = 5,

    /// <summary>A page the agent added. The only kind that can be deleted.</summary>
    Custom = 6,
}

/// <summary>The four kinds of block a page is built from.</summary>
/// <remarks>
/// Plan §2.4 lists seven. The MVP ships these four (docs/BUILD_PLAN.md); gallery, trip-request and FAQ
/// blocks come later as new values here, and a renderer that meets a type it does not know skips it.
/// </remarks>
public enum SiteBlockType
{
    Hero = 1,

    /// <summary>A grid of the agency's published products. Holds a selection, never a price.</summary>
    ProductGrid = 2,

    /// <summary>A heading and paragraphs of plain text.</summary>
    Text = 3,

    /// <summary>How to reach the agency, from its branding.</summary>
    Contact = 4,
}

/// <summary>A free address under the platform's zone, or one the agency owns.</summary>
public enum SiteDomainType
{
    /// <summary><c>agency.&lt;base domain&gt;</c>. Created with the site; we own the zone.</summary>
    Subdomain = 1,

    /// <summary>The agency's own hostname. Proven with DNS records before it serves.</summary>
    Custom = 2,
}

/// <summary>Whether we have proven the agency controls a custom hostname.</summary>
public enum DomainVerificationStatus
{
    /// <summary>Waiting for the DNS records. Checked on a backing-off schedule.</summary>
    Pending = 1,

    /// <summary>Both records found. The hostname serves the site.</summary>
    Verified = 2,

    /// <summary>Seven days passed without the records appearing. The agent can start again.</summary>
    Abandoned = 3,
}

/// <summary>Whether a hostname has a certificate, so it can serve over HTTPS.</summary>
public enum SslCertificateStatus
{
    None = 1,
    Pending = 2,
    Issued = 3,

    /// <summary>Issuance gave up. A person has been alerted.</summary>
    Failed = 4,

    /// <summary>It lapsed without a renewal. The site shows a browser warning until it is fixed.</summary>
    Expired = 5,
}

/// <summary>The DNS record a check looked for.</summary>
public enum DnsRecordKind
{
    /// <summary>The ownership proof, at <c>_storefront-verify.&lt;host&gt;</c>.</summary>
    Txt = 1,

    /// <summary>The routing record that sends the host's traffic to us.</summary>
    Cname = 2,
}

/// <summary>What one DNS lookup found.</summary>
public enum DnsCheckOutcome
{
    Match = 1,
    Mismatch = 2,

    /// <summary>The name does not exist, or holds no record of the kind asked for.</summary>
    NotFound = 3,

    Timeout = 4,
    ServerFailure = 5,
    Error = 6,
}

/// <summary>How a listed hostname label is treated when an agency claims it (open question 20).</summary>
public enum ReservedHostnameKind
{
    /// <summary>Refused outright: <c>www</c>, <c>admin</c>, the platform's own name.</summary>
    Reserved = 1,

    /// <summary>A well-known brand. A lookalike claim is accepted and set aside for a person to review.</summary>
    Brand = 2,
}
