namespace TripsAgent.Contracts.Storefront;

/// <summary>
/// What a published version of a site says: its settings, the agency's contact details and every
/// page. Frozen when the draft is staged, and exactly what the storefront renders.
/// </summary>
/// <remarks>
/// <para>
/// This is the contract between the website builder and the storefront, and it is stored as JSON in
/// <c>site_versions.content_snapshot</c> for as long as the version exists. Changing its shape is
/// deliberate: bump <see cref="SchemaVersion"/>, keep reading the old one, and update the golden-file
/// test that pins it.
/// </para>
/// <para>
/// Nothing in it is margin, an internal id the traveller should not see, or the platform's brand.
/// </para>
/// </remarks>
/// <param name="SchemaVersion">The shape of this snapshot. 1 today.</param>
public sealed record SiteContentSnapshot(
    int SchemaVersion,
    SiteSnapshotSettings Site,
    SiteSnapshotBusiness Business,
    IReadOnlyList<SiteSnapshotPage> Pages);

/// <summary>How a published version of a site looks. Stored in <c>site_versions.theme_snapshot</c>.</summary>
/// <param name="TemplateCode">The layout the storefront renders: <c>horizon</c>, <c>harbour</c>, <c>atlas</c>.</param>
/// <param name="LogoAssetId">The agency's logo, served from the site's own domain. Null for a text logo.</param>
/// <param name="PrimaryColor">Hex. Buttons, links and the header use it, always with white text.</param>
/// <param name="HeadingFont">One the storefront ships: <c>inter</c> or <c>lora</c>.</param>
public sealed record SiteThemeSnapshot(
    int SchemaVersion,
    string TemplateCode,
    Guid? LogoAssetId,
    string PrimaryColor,
    string? SecondaryColor,
    string HeadingFont,
    string BodyFont);

/// <summary>The site-wide settings in a snapshot.</summary>
/// <param name="Name">The agency's name as its site shows it — in the header, the footer, every title.</param>
/// <param name="Language">The <c>lang</c> of every page.</param>
public sealed record SiteSnapshotSettings(
    string Name,
    string Language,
    string? SeoTitle,
    string? SeoDescription,
    bool FlightSearchEnabled);

/// <summary>How travellers reach the agency, from its branding.</summary>
public sealed record SiteSnapshotBusiness(
    string? ContactAddress,
    string? Email,
    string? Phone,
    string? WhatsApp,
    IReadOnlyList<SiteSocialLinkDto> SocialLinks);

/// <summary>One of the agency's social profiles.</summary>
/// <param name="Network"><c>instagram</c>, <c>facebook</c>, <c>x</c>, <c>tiktok</c>, <c>youtube</c> or <c>linkedin</c>.</param>
/// <param name="Url">An <c>https://</c> address.</param>
public sealed record SiteSocialLinkDto(string Network, string Url);

/// <summary>One page, with its blocks in the order they appear.</summary>
/// <param name="Slug">The page's address; <c>home</c> is served at <c>/</c>.</param>
/// <param name="PageType"><c>Home</c>, <c>About</c>, <c>Contact</c>, <c>Terms</c>, <c>Catalog</c> or <c>Custom</c>.</param>
/// <param name="Position">Its place in the navigation.</param>
public sealed record SiteSnapshotPage(
    string Slug,
    string PageType,
    string Title,
    bool ShowInNav,
    int Position,
    string? MetaTitle,
    string? MetaDescription,
    IReadOnlyList<SiteBlockDto> Blocks);

/// <summary>
/// Everything the storefront needs to render one hostname's site, from the anonymous public API.
/// </summary>
/// <remarks>
/// The site is found from the request's <c>Host</c> and from nothing else — no query parameter, no
/// header an outsider can set, no path segment can choose which agency is served.
/// </remarks>
/// <param name="Status">
/// <c>Live</c> — a published version. <c>OpeningSoon</c> — a site that has not published yet, shown in
/// the agency's branding. <c>Offline</c> — the agency is suspended, and the site shows a maintenance
/// page. <c>Preview</c> — an unpublished version, through a signed preview link.
/// </param>
/// <param name="SiteId">A stable key for the storefront's cache tags. Not secret, and not guessable.</param>
/// <param name="Hostname">The host that was asked for.</param>
/// <param name="PrimaryHostname">The host canonical URLs point at.</param>
/// <param name="Indexable">Whether search engines may index this host: a live site on its main address.</param>
/// <param name="Content">Pages are empty unless the site is live or previewed.</param>
/// <param name="Images">
/// A signed link for every image the site refers to by asset id — its logo and each hero — keyed by
/// that id as a string. The snapshot keeps ids rather than links because a link is signed and expires;
/// this map is built fresh on every request so the renderer never has to ask for one itself, and an
/// image that has not been scanned clean is simply missing from it.
/// </param>
public sealed record PublicSiteResponse(
    string Status,
    Guid SiteId,
    string Hostname,
    string PrimaryHostname,
    bool IsPrimaryHostname,
    bool Indexable,
    int? VersionNumber,
    DateTimeOffset? PublishedAt,
    SiteContentSnapshot Content,
    SiteThemeSnapshot Theme,
    IReadOnlyDictionary<string, string> Images);
