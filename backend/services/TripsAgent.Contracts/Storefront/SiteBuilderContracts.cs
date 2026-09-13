namespace TripsAgent.Contracts.Storefront;

/// <summary>A starter website an agency can choose.</summary>
/// <param name="PageTitles">The pages a site made from it starts with.</param>
public sealed record SiteTemplateResponse(
    string Code,
    string Name,
    string Description,
    IReadOnlyList<string> PageTitles);

/// <summary>Creates the agency's website from a template.</summary>
public sealed record CreateSiteRequest(string TemplateCode);

/// <summary>The agency's website as the builder shows it.</summary>
/// <param name="Status"><c>Draft</c> until the first publish, then <c>Published</c>.</param>
/// <param name="HasUnstagedChanges">The draft differs from the version last staged or published.</param>
/// <param name="HasUnpublishedChanges">The draft differs from what travellers see now.</param>
/// <param name="PublishCheck">Whether a staged version could be published, and if not, why.</param>
/// <param name="SiteUrl">Where the live site is, on its main address. Null only while the site has no address.</param>
public sealed record SiteResponse(
    Guid Id,
    string Status,
    string TemplateCode,
    string TemplateName,
    SiteSettingsResponse Settings,
    IReadOnlyList<SitePageSummaryResponse> Pages,
    SiteVersionResponse? Staged,
    SiteVersionResponse? Published,
    bool HasUnstagedChanges,
    bool HasUnpublishedChanges,
    SitePublishCheckResponse PublishCheck,
    string? PrimaryHostname,
    string? SiteUrl);

/// <summary>The site's name, search details, contact details and flight sales.</summary>
/// <remarks>
/// The contact details and social links belong to the agency's branding, which invoices and emails
/// read too; they are edited here because this is where an agent thinks of them.
/// </remarks>
public sealed record SiteSettingsResponse(
    string Name,
    string? SeoTitle,
    string? SeoDescription,
    bool FlightSearchEnabled,
    string? ContactEmail,
    string? ContactPhone,
    string? WhatsAppNumber,
    string? ContactAddress,
    IReadOnlyList<SiteSocialLinkDto> SocialLinks);

/// <summary>Changes the site's settings. Takes effect on the live site at the next publish.</summary>
public sealed record SiteSettingsRequest(
    string Name,
    string? SeoTitle,
    string? SeoDescription,
    bool FlightSearchEnabled,
    string? ContactEmail,
    string? ContactPhone,
    string? WhatsAppNumber,
    string? ContactAddress,
    IReadOnlyList<SiteSocialLinkDto> SocialLinks);

/// <summary>A page in the site's list.</summary>
public sealed record SitePageSummaryResponse(
    Guid Id,
    string Slug,
    string PageType,
    string Title,
    bool IsSystem,
    bool ShowInNav,
    int Position,
    int BlockCount);

/// <summary>One page of the draft, with every block, for the page editor.</summary>
/// <param name="Revision">Send it back with a save. A save naming an older revision is refused with 409.</param>
public sealed record SitePageResponse(
    Guid Id,
    string Slug,
    string PageType,
    string Title,
    bool IsSystem,
    bool ShowInNav,
    int Position,
    string? MetaTitle,
    string? MetaDescription,
    int Revision,
    IReadOnlyList<SitePageBlockResponse> Blocks);

/// <summary>A block on a page, with the id the editor keys it by.</summary>
public sealed record SitePageBlockResponse(Guid Id, SiteBlockDto Block);

/// <summary>
/// Saves a whole page: its details and every block, in order. Reordering, adding and removing blocks
/// are all this one request, so a save either happens entirely or not at all.
/// </summary>
/// <param name="Slug">Ignored for the home page, whose address is fixed.</param>
/// <param name="Revision">The revision the editor loaded. A different one means someone else saved first.</param>
public sealed record SaveSitePageRequest(
    string Title,
    string? Slug,
    bool ShowInNav,
    string? MetaTitle,
    string? MetaDescription,
    int Revision,
    IReadOnlyList<SitePageBlockRequest> Blocks);

/// <summary>A block in a page save.</summary>
/// <param name="Id">The block's id when it is already on the page; null for a new one.</param>
public sealed record SitePageBlockRequest(Guid? Id, SiteBlockDto Block);

/// <summary>Adds a page of the agent's own.</summary>
/// <param name="Slug">Made from the title when left out.</param>
public sealed record CreateSitePageRequest(string Title, string? Slug);

/// <summary>One version of the site, for the publishing panel and the history.</summary>
/// <param name="StagedBy">The name of whoever staged it.</param>
/// <param name="PublishedAt">The last time it went live.</param>
/// <param name="CanRollBackTo">It has been live before and could be put back.</param>
/// <param name="IsLive">It is what travellers see now.</param>
public sealed record SiteVersionResponse(
    Guid Id,
    int VersionNumber,
    string Status,
    DateTimeOffset? StagedAt,
    string? StagedBy,
    DateTimeOffset? PublishedAt,
    string? PublishedBy,
    bool CanRollBackTo,
    bool IsLive);

/// <summary>Whether the site may be published, and every reason it may not.</summary>
public sealed record SitePublishCheckResponse(bool CanPublish, IReadOnlyList<SitePublishProblemResponse> Problems);

/// <summary>One reason a site cannot be published yet.</summary>
/// <param name="Code"><c>agency-not-verified</c> or <c>nothing-to-sell</c>.</param>
public sealed record SitePublishProblemResponse(string Code, string Message);

/// <summary>Asks for a preview link.</summary>
/// <param name="VersionId">A version of the site; null for the draft as it is right now.</param>
public sealed record SitePreviewLinkRequest(Guid? VersionId);

/// <summary>A short-lived link that shows an unpublished version of the site.</summary>
/// <param name="Url">On the site's own main address. Signed, and never indexed.</param>
public sealed record SitePreviewLinkResponse(string Url, DateTimeOffset ExpiresAt);

/// <summary>The site's look: the agency's logo and colours.</summary>
/// <param name="LogoPreviewUrl">A short-lived link to the logo, for the console to show.</param>
/// <param name="PrimaryColor">Hex. Buttons, links and the header put white text on it.</param>
public sealed record SiteThemeResponse(
    Guid? LogoAssetId,
    string? LogoPreviewUrl,
    string PrimaryColor,
    string? SecondaryColor);

/// <summary>Changes the site's look. The logo and colours are the agency's, so invoices change too.</summary>
/// <param name="LogoAssetId">An uploaded logo that has been scanned clean; null for a text logo.</param>
/// <param name="PrimaryColor">Hex. Refused when white text on it would be hard to read.</param>
public sealed record SiteThemeRequest(Guid? LogoAssetId, string PrimaryColor, string? SecondaryColor);
