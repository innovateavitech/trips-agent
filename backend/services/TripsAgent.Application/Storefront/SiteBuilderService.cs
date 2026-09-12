using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Assets;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Contracts.Storefront;
using TripsAgent.Domain.Assets;
using TripsAgent.Domain.Platform;
using TripsAgent.Domain.Storefront;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.Application.Storefront;

/// <summary>
/// The website builder's editing side: templates, creating the site, its settings, its look and its
/// pages. Every write goes to the draft; nothing here changes what travellers see until a publish.
/// </summary>
public sealed partial class SiteBuilderService
{
    private const string NoSite = "You have not created a website yet.";
    private const string NoPage = "There is no page with that id on your website.";
    private const string ChangedElsewhere = "Someone else changed this just now.";
    private const string ReloadAdvice = "Your changes were not saved. Reload to see the latest version, then make your changes again.";

    /// <summary>How much of a long business name fits into template headings.</summary>
    private const int MaxNameInTemplateText = 40;

    private readonly IAppDbContext _db;
    private readonly SiteQueries _queries;
    private readonly IPlatformScope _platformScope;
    private readonly IUniqueViolationDetector _uniqueViolations;
    private readonly AssetDelivery _delivery;
    private readonly StorefrontOptions _options;
    private readonly TimeProvider _clock;

    public SiteBuilderService(
        IAppDbContext db,
        SiteQueries queries,
        IPlatformScope platformScope,
        IUniqueViolationDetector uniqueViolations,
        AssetDelivery delivery,
        StorefrontOptions options,
        TimeProvider clock)
    {
        _db = db;
        _queries = queries;
        _platformScope = platformScope;
        _uniqueViolations = uniqueViolations;
        _delivery = delivery;
        _options = options;
        _clock = clock;
    }

    // ------------------------------------------------------------------ templates

    public async Task<IReadOnlyList<SiteTemplateResponse>> ListTemplatesAsync(CancellationToken cancellationToken = default)
    {
        var templates = await _db.SiteTemplates.AsNoTracking()
            .Where(template => template.IsActive)
            .OrderBy(template => template.Name)
            .ToListAsync(cancellationToken);

        return templates
            .Select(template => new SiteTemplateResponse(
                template.Code,
                template.Name,
                template.Description,
                SiteTemplateCatalog.ReadSchema(template.BlockSchema).Pages.Select(page => page.Title).ToList()))
            .ToList();
    }

    // ------------------------------------------------------------------ the site

    public async Task<StorefrontResult<SiteResponse>> GetSiteAsync(CancellationToken cancellationToken = default)
    {
        var site = await _db.Sites.AsNoTracking().FirstOrDefaultAsync(cancellationToken);

        return site is null
            ? new StorefrontResult<SiteResponse>.NotFound(NoSite)
            : new StorefrontResult<SiteResponse>.Ok(await DescribeAsync(site, cancellationToken));
    }

    /// <summary>
    /// Creates the agency's website from a template: the site, its draft with the template's pages, its
    /// theme, and its free address — in one save.
    /// </summary>
    public async Task<StorefrontResult<SiteResponse>> CreateSiteAsync(CreateSiteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var agencyId = _queries.AgencyId;
        var agency = await _db.Agencies.AsNoTracking().FirstAsync(candidate => candidate.Id == agencyId, cancellationToken);

        // Open question 6, as decided for the MVP: one site per tenant, and a sub-agent sells under its
        // principal's brand rather than its own.
        if (agency.Type == AgencyType.SubAgent)
        {
            return new StorefrontResult<SiteResponse>.Refused(
                "Sub-agents sell through their principal's website.",
                "Your customers book on your principal agency's website, so a sub-agent does not have one of its own.",
                []);
        }

        if (await _db.Sites.AnyAsync(cancellationToken))
        {
            return new StorefrontResult<SiteResponse>.Conflict(
                "You already have a website.",
                "Each agency has one website. Edit it rather than starting another.");
        }

        var code = request.TemplateCode?.Trim().ToLowerInvariant() ?? string.Empty;
        var template = await _db.SiteTemplates.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Code == code && candidate.IsActive, cancellationToken);

        if (template is null)
        {
            return new StorefrontResult<SiteResponse>.NotFound("There is no website template with that name.");
        }

        var now = _clock.GetUtcNow();
        var name = agency.TradingName ?? agency.LegalName;
        var shortName = name.Length <= MaxNameInTemplateText ? name : name[..MaxNameInTemplateText].TrimEnd();

        // Loaded for its side effect: an agency without branding gets neutral defaults in this save.
        await _queries.BrandingAsync(tracked: true, cancellationToken);

        var site = Site.Create(agencyId, template.Id, name);
        var draft = SiteVersion.CreateDraft(site);
        site.AttachDraft(draft);

        var pages = SiteTemplateCatalog.ReadSchema(template.BlockSchema).Pages
            .Select((definition, position) => BuildPage(draft, definition, position, shortName, template.Code))
            .ToList();

        var (hostname, needsReview) = await ChooseFreeHostnameAsync(agency.Slug, cancellationToken);
        var subdomain = SiteDomain.ForSubdomain(site, hostname, now);

        if (needsReview)
        {
            // Open question 20: a name that looks like a well-known brand is set aside, not refused.
            subdomain.FlagForReview();
            _db.AdminAlerts.Add(AdminAlert.ForHostnameReview(agencyId, subdomain.Id, hostname));
        }

        site.SetPrimaryDomain(subdomain);

        _db.Sites.Add(site);
        _db.SiteVersions.Add(draft);
        _db.SiteThemes.Add(SiteTheme.Create(site, SiteFonts.DefaultHeading));
        _db.SitePages.AddRange(pages);
        _db.SiteDomains.Add(subdomain);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (_uniqueViolations.IsUniqueViolation(ex))
        {
            _db.ChangeTracker.Clear();

            return new StorefrontResult<SiteResponse>.Conflict(
                "Your website could not be created just now.",
                "Someone else created one at the same moment, or its address was taken. Reload and try again.");
        }

        return new StorefrontResult<SiteResponse>.Ok(await DescribeAsync(site, cancellationToken));
    }

    /// <summary>Changes the site's name, search details, contact details and flight sales.</summary>
    public async Task<StorefrontResult<SiteResponse>> UpdateSettingsAsync(SiteSettingsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var site = await _db.Sites.FirstOrDefaultAsync(cancellationToken);

        if (site is null)
        {
            return new StorefrontResult<SiteResponse>.NotFound(NoSite);
        }

        var errors = new FieldErrors();
        var name = request.Name?.Trim();

        if (string.IsNullOrEmpty(name))
        {
            errors.Add("name", "Your site needs a name.");
        }

        CheckLength(name, Site.MaxNameLength, "name", errors);
        CheckLength(request.SeoTitle, Site.MaxSeoTitleLength, "seoTitle", errors);
        CheckLength(request.SeoDescription, Site.MaxSeoDescriptionLength, "seoDescription", errors);
        CheckLength(request.ContactAddress, 500, "contactAddress", errors);

        if (!string.IsNullOrWhiteSpace(request.ContactEmail) && !AgencyBranding.IsValidContactEmail(request.ContactEmail))
        {
            errors.Add("contactEmail", "That is not an email address, e.g. hello@yourbusiness.com.");
        }

        if (!string.IsNullOrWhiteSpace(request.ContactPhone) && !AgencyBranding.IsValidPhoneNumber(request.ContactPhone))
        {
            errors.Add("contactPhone", "That is not a phone number, e.g. +234 803 123 4567.");
        }

        if (!string.IsNullOrWhiteSpace(request.WhatsAppNumber) && !AgencyBranding.IsValidPhoneNumber(request.WhatsAppNumber))
        {
            errors.Add("whatsAppNumber", "That is not a WhatsApp number, e.g. +234 803 123 4567.");
        }

        var links = ReadSocialLinks(request.SocialLinks, errors);

        if (errors.Any)
        {
            return new StorefrontResult<SiteResponse>.Invalid(errors.ToDictionary());
        }

        var branding = await _queries.BrandingAsync(tracked: true, cancellationToken);

        site.UpdateSettings(name!, request.SeoTitle, request.SeoDescription, request.FlightSearchEnabled);
        branding.SetContactAddress(request.ContactAddress);
        branding.SetContactDetails(request.ContactEmail, request.ContactPhone, request.WhatsAppNumber);
        branding.SetSocialLinks(links);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.ChangeTracker.Clear();
            return new StorefrontResult<SiteResponse>.Conflict(ChangedElsewhere, ReloadAdvice);
        }

        return new StorefrontResult<SiteResponse>.Ok(await DescribeAsync(site, cancellationToken));
    }

    // ------------------------------------------------------------------ the look

    public async Task<StorefrontResult<SiteThemeResponse>> GetThemeAsync(CancellationToken cancellationToken = default)
    {
        if (!await _db.Sites.AnyAsync(cancellationToken))
        {
            return new StorefrontResult<SiteThemeResponse>.NotFound(NoSite);
        }

        var branding = await _queries.BrandingAsync(tracked: false, cancellationToken);

        return new StorefrontResult<SiteThemeResponse>.Ok(await ThemeOfAsync(branding, cancellationToken));
    }

    /// <summary>
    /// Changes the logo and colours. They are the agency's branding, so its invoices and emails change
    /// with them — the logo and colours are defined once.
    /// </summary>
    public async Task<StorefrontResult<SiteThemeResponse>> SaveThemeAsync(SiteThemeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await _db.Sites.AnyAsync(cancellationToken))
        {
            return new StorefrontResult<SiteThemeResponse>.NotFound(NoSite);
        }

        var errors = new FieldErrors();
        var primary = request.PrimaryColor?.Trim();

        if (primary is null || !HexColourPattern().IsMatch(primary))
        {
            errors.Add("primaryColor", "Choose a colour, written like #1F2933.");
        }
        else if (!ColorContrast.PassesWithWhiteText(primary))
        {
            var ratio = ColorContrast.RatioAgainstWhite(primary).ToString("0.0", CultureInfo.InvariantCulture);

            errors.Add(
                "primaryColor",
                $"White text on this colour is hard to read (contrast {ratio} to 1; it needs at least 4.5). Choose a darker shade.");
        }

        var secondary = string.IsNullOrWhiteSpace(request.SecondaryColor) ? null : request.SecondaryColor.Trim();

        if (secondary is not null && !HexColourPattern().IsMatch(secondary))
        {
            errors.Add("secondaryColor", "Choose a colour, written like #1F2933.");
        }

        if (request.LogoAssetId is { } logoId)
        {
            await CheckLogoAsync(logoId, errors, cancellationToken);
        }

        if (errors.Any)
        {
            return new StorefrontResult<SiteThemeResponse>.Invalid(errors.ToDictionary());
        }

        var branding = await _queries.BrandingAsync(tracked: true, cancellationToken);
        branding.SetColors(primary!, secondary);
        branding.SetLogo(request.LogoAssetId);

        await _db.SaveChangesAsync(cancellationToken);

        return new StorefrontResult<SiteThemeResponse>.Ok(await ThemeOfAsync(branding, cancellationToken));
    }

    // ------------------------------------------------------------------ pages

    public async Task<StorefrontResult<SitePageResponse>> GetPageAsync(Guid pageId, CancellationToken cancellationToken = default)
    {
        var draftId = await DraftVersionIdAsync(cancellationToken);

        if (draftId is null)
        {
            return new StorefrontResult<SitePageResponse>.NotFound(NoSite);
        }

        var page = await _db.SitePages.AsNoTracking()
            .Include(candidate => candidate.Blocks)
            .FirstOrDefaultAsync(candidate => candidate.Id == pageId && candidate.VersionId == draftId, cancellationToken);

        return page is null
            ? new StorefrontResult<SitePageResponse>.NotFound(NoPage)
            : new StorefrontResult<SitePageResponse>.Ok(ToPageResponse(page));
    }

    /// <summary>Adds a page of the agent's own to the draft, with one text block to start from.</summary>
    public async Task<StorefrontResult<SitePageResponse>> CreatePageAsync(CreateSitePageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var draftId = await DraftVersionIdAsync(cancellationToken);

        if (draftId is null)
        {
            return new StorefrontResult<SitePageResponse>.NotFound(NoSite);
        }

        var draft = await _db.SiteVersions.AsNoTracking().FirstAsync(version => version.Id == draftId, cancellationToken);
        var existing = await _db.SitePages.AsNoTracking()
            .Where(page => page.VersionId == draftId)
            .Select(page => new { page.Slug, page.Position })
            .ToListAsync(cancellationToken);

        var errors = new FieldErrors();
        var title = request.Title?.Trim();

        if (string.IsNullOrEmpty(title))
        {
            errors.Add("title", "A page needs a title.");
        }

        CheckLength(title, SitePageRules.MaxTitleLength, "title", errors);

        var slugSource = string.IsNullOrWhiteSpace(request.Slug) ? SitePageRules.SlugFrom(title ?? string.Empty) : request.Slug;

        if (!SitePageRules.TryNormaliseSlug(slugSource, out var slug, out var problem))
        {
            errors.Add("slug", problem);
        }
        else if (existing.Any(page => string.Equals(page.Slug, slug, StringComparison.Ordinal)))
        {
            errors.Add("slug", "Another page already uses that address.");
        }

        if (existing.Count >= SitePageRules.MaxPages)
        {
            errors.Add("title", $"A site can have at most {SitePageRules.MaxPages} pages.");
        }

        if (errors.Any)
        {
            return new StorefrontResult<SitePageResponse>.Invalid(errors.ToDictionary());
        }

        var position = existing.Select(page => page.Position).DefaultIfEmpty(-1).Max() + 1;
        var page = SitePage.Create(draft, SitePageType.Custom, slug, title!, showInNav: true, position);
        page.ReplaceBlocks(
        [
            new SiteBlockContent(null, SiteBlockType.Text, SiteBlocks.ToStored(SiteBlocks.Text(new TextBlockConfig(null, "Add your text here.")))),
        ]);

        _db.SitePages.Add(page);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (_uniqueViolations.IsUniqueViolation(ex))
        {
            _db.ChangeTracker.Clear();
            return new StorefrontResult<SitePageResponse>.Conflict(ChangedElsewhere, ReloadAdvice);
        }

        return new StorefrontResult<SitePageResponse>.Ok(ToPageResponse(page));
    }

    /// <summary>
    /// Saves a whole page — its details and every block, in order — in one transaction. A save naming an
    /// older revision is refused, so a second browser tab cannot overwrite the first.
    /// </summary>
    public async Task<StorefrontResult<SitePageResponse>> SavePageAsync(
        Guid pageId,
        SaveSitePageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var draftId = await DraftVersionIdAsync(cancellationToken);

        if (draftId is null)
        {
            return new StorefrontResult<SitePageResponse>.NotFound(NoSite);
        }

        var page = await _db.SitePages
            .Include(candidate => candidate.Blocks)
            .FirstOrDefaultAsync(candidate => candidate.Id == pageId && candidate.VersionId == draftId, cancellationToken);

        if (page is null)
        {
            return new StorefrontResult<SitePageResponse>.NotFound(NoPage);
        }

        if (request.Revision != page.Revision)
        {
            return new StorefrontResult<SitePageResponse>.Conflict(ChangedElsewhere, ReloadAdvice);
        }

        var errors = new FieldErrors();
        var title = request.Title?.Trim();

        if (string.IsNullOrEmpty(title))
        {
            errors.Add("title", "A page needs a title.");
        }

        CheckLength(title, SitePageRules.MaxTitleLength, "title", errors);
        CheckLength(request.MetaTitle, SitePageRules.MaxMetaTitleLength, "metaTitle", errors);
        CheckLength(request.MetaDescription, SitePageRules.MaxMetaDescriptionLength, "metaDescription", errors);

        string? slug = null;

        if (page.PageType != SitePageType.Home)
        {
            if (!SitePageRules.TryNormaliseSlug(request.Slug ?? page.Slug, out var normalised, out var problem))
            {
                errors.Add("slug", problem);
            }
            else if (await _db.SitePages.AnyAsync(
                         other => other.VersionId == draftId && other.Id != pageId && other.Slug == normalised,
                         cancellationToken))
            {
                errors.Add("slug", "Another page already uses that address.");
            }
            else
            {
                slug = normalised;
            }
        }

        var requested = request.Blocks ?? [];
        var validated = SiteBlockValidator.Validate(requested.Select(block => block?.Block).ToList(), "blocks", errors);

        await _queries.CheckImagesAsync(validated.Images, errors, cancellationToken);
        await _queries.CheckProductsAsync(validated.Products, errors, cancellationToken);

        if (errors.Any)
        {
            return new StorefrontResult<SitePageResponse>.Invalid(errors.ToDictionary());
        }

        page.UpdateDetails(title!, slug, request.ShowInNav, request.MetaTitle, request.MetaDescription);

        // With no errors, every requested block validated, so the two lists line up one for one.
        page.ReplaceBlocks(validated.Blocks
            .Select((block, index) => new SiteBlockContent(requested[index]?.Id, block.Type, SiteBlocks.ToStored(block.Block)))
            .ToList());

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.ChangeTracker.Clear();
            return new StorefrontResult<SitePageResponse>.Conflict(ChangedElsewhere, ReloadAdvice);
        }
        catch (DbUpdateException ex) when (_uniqueViolations.IsUniqueViolation(ex))
        {
            _db.ChangeTracker.Clear();
            return new StorefrontResult<SitePageResponse>.Conflict(ChangedElsewhere, ReloadAdvice);
        }

        return new StorefrontResult<SitePageResponse>.Ok(ToPageResponse(page));
    }

    /// <summary>Removes one of the agent's own pages from the draft. System pages stay.</summary>
    public async Task<StorefrontResult<bool>> DeletePageAsync(Guid pageId, CancellationToken cancellationToken = default)
    {
        var draftId = await DraftVersionIdAsync(cancellationToken);

        if (draftId is null)
        {
            return new StorefrontResult<bool>.NotFound(NoSite);
        }

        var page = await _db.SitePages.FirstOrDefaultAsync(
            candidate => candidate.Id == pageId && candidate.VersionId == draftId,
            cancellationToken);

        if (page is null)
        {
            return new StorefrontResult<bool>.NotFound(NoPage);
        }

        if (page.IsSystem)
        {
            return new StorefrontResult<bool>.Conflict(
                "This page cannot be removed.",
                "Every site keeps its home, about, contact, terms and catalog pages. You can hide it from the menu instead.");
        }

        _db.SitePages.Remove(page);
        await _db.SaveChangesAsync(cancellationToken);

        return new StorefrontResult<bool>.Ok(true);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The site as the builder shows it, with what has changed since it was last staged and published.</summary>
    private async Task<SiteResponse> DescribeAsync(Site site, CancellationToken cancellationToken)
    {
        var template = await _db.SiteTemplates.AsNoTracking().FirstAsync(candidate => candidate.Id == site.TemplateId, cancellationToken);
        var pages = await _queries.DraftPagesAsync(site, tracked: false, cancellationToken);
        var branding = await _queries.BrandingAsync(tracked: false, cancellationToken);
        var theme = await _db.SiteThemes.AsNoTracking().FirstOrDefaultAsync(candidate => candidate.SiteId == site.Id, cancellationToken);

        var versions = await _db.SiteVersions.AsNoTracking()
            .Where(version => version.SiteId == site.Id
                              && (version.Status == SiteVersionStatus.Staged || version.Id == site.PublishedVersionId))
            .ToListAsync(cancellationToken);

        var staged = versions.FirstOrDefault(version => version.Status == SiteVersionStatus.Staged);
        var published = versions.FirstOrDefault(version => version.Id == site.PublishedVersionId);
        var names = await _queries.UserNamesAsync(
            versions.SelectMany(version => new[] { version.StagedByUserId, version.PublishedByUserId }),
            cancellationToken);

        var draftContent = SiteSnapshots.Canonical(SiteSnapshots.BuildContent(site, branding, pages));
        var draftTheme = SiteSnapshots.Canonical(SiteSnapshots.BuildTheme(template.Code, branding, theme));

        bool Matches(SiteVersion? version) =>
            version is not null
            && SiteSnapshots.ReadContent(version.ContentSnapshot) is { } content
            && string.Equals(SiteSnapshots.Canonical(content), draftContent, StringComparison.Ordinal)
            && SiteSnapshots.ReadTheme(version.ThemeSnapshot) is { } look
            && string.Equals(SiteSnapshots.Canonical(look), draftTheme, StringComparison.Ordinal);

        var problems = SitePublishGate.Check(await _queries.GateFactsAsync(site, cancellationToken));

        var primary = site.PrimaryDomainId is { } domainId
            ? await _db.SiteDomains.AsNoTracking()
                .Where(domain => domain.Id == domainId)
                .Select(domain => domain.Hostname)
                .FirstOrDefaultAsync(cancellationToken)
            : null;

        return new SiteResponse(
            site.Id,
            site.Status.ToString(),
            template.Code,
            template.Name,
            new SiteSettingsResponse(
                site.Name,
                site.SeoTitle,
                site.SeoDescription,
                site.FlightSearchEnabled,
                branding.ContactEmail,
                branding.ContactPhone,
                branding.WhatsAppNumber,
                branding.ContactAddress,
                SiteSnapshots.SocialLinksOf(branding)),
            pages.Select(page => new SitePageSummaryResponse(
                    page.Id,
                    page.Slug,
                    page.PageType.ToString(),
                    page.Title,
                    page.IsSystem,
                    page.ShowInNav,
                    page.Position,
                    page.Blocks.Count))
                .ToList(),
            staged is null ? null : SiteQueries.ToResponse(staged, site, names),
            published is null ? null : SiteQueries.ToResponse(published, site, names),
            HasUnstagedChanges: !Matches(staged ?? published),
            HasUnpublishedChanges: !Matches(published),
            new SitePublishCheckResponse(
                problems.Count == 0,
                problems.Select(problem => new SitePublishProblemResponse(problem.Code, problem.Message)).ToList()),
            primary,
            primary is null ? null : _options.SiteUrlFor(primary));
    }

    private static SitePage BuildPage(
        SiteVersion draft,
        SiteTemplatePageDefinition definition,
        int position,
        string businessName,
        string templateCode)
    {
        var page = SitePage.Create(
            draft,
            Enum.Parse<SitePageType>(definition.PageType),
            definition.Slug,
            definition.Title,
            definition.ShowInNav,
            position);

        var errors = new FieldErrors();
        var blocks = SiteBlockValidator.Validate(
            definition.Blocks.Select(block => (SiteBlockDto?)SiteTemplateCatalog.Personalise(block, businessName)).ToList(),
            "blocks",
            errors);

        if (errors.Any)
        {
            throw new InvalidOperationException(
                $"Template '{templateCode}' has a page ({definition.Slug}) whose blocks do not validate: "
                + string.Join("; ", errors.ToDictionary().SelectMany(pair => pair.Value)));
        }

        page.ReplaceBlocks(blocks.Blocks
            .Select(block => new SiteBlockContent(null, block.Type, SiteBlocks.ToStored(block.Block)))
            .ToList());

        return page;
    }

    /// <summary>
    /// The first free address for the agency: its slug under the platform's zone, moved aside if it is a
    /// reserved word, numbered if it is taken. Reports whether it looks like a well-known brand.
    /// </summary>
    private async Task<(string Hostname, bool NeedsReview)> ChooseFreeHostnameAsync(string agencySlug, CancellationToken cancellationToken)
    {
        var listed = await _db.ReservedHostnameLabels.AsNoTracking()
            .Select(label => new { label.Label, label.Kind })
            .ToListAsync(cancellationToken);

        var reserved = listed.Where(label => label.Kind == ReservedHostnameKind.Reserved).Select(label => label.Label).ToList();
        var brands = listed.Where(label => label.Kind == ReservedHostnameKind.Brand).Select(label => label.Label).ToList();

        var label = FreeSubdomains.BaseLabel(agencySlug);

        if (ReservedHostnames.IsReserved(label, reserved))
        {
            label = FreeSubdomains.MovedAside(label);
        }

        List<string> taken;

        // The one cross-tenant read in the builder, and all it returns is hostnames: the address must
        // be unique across every agency, and the tenant filter would hide the ones it collides with.
        using (_platformScope.Enter("free subdomain — finds the first address under the platform's zone that no agency holds"))
        {
            var prefix = label;
            taken = await _db.SiteDomains.AsNoTracking()
                .Where(domain => domain.Hostname.StartsWith(prefix))
                .Select(domain => domain.Hostname)
                .ToListAsync(cancellationToken);
        }

        foreach (var candidate in FreeSubdomains.Candidates(label))
        {
            var hostname = $"{candidate}.{_options.SubdomainBaseDomain}";

            if (!taken.Contains(hostname, StringComparer.OrdinalIgnoreCase)
                && Hostnames.NormaliseHostHeader(hostname) is { } normalised)
            {
                return (normalised, ReservedHostnames.LooksLikeBrand(candidate, brands));
            }
        }

        throw new InvalidOperationException($"No free address could be found for '{agencySlug}'.");
    }

    private Task<Guid?> DraftVersionIdAsync(CancellationToken cancellationToken) =>
        _db.Sites.AsNoTracking().Select(site => site.DraftVersionId).FirstOrDefaultAsync(cancellationToken);

    private async Task<SiteThemeResponse> ThemeOfAsync(AgencyBranding branding, CancellationToken cancellationToken)
    {
        string? preview = null;

        if (branding.LogoAssetId is { } logoId)
        {
            var asset = await _db.Assets.AsNoTracking().FirstOrDefaultAsync(candidate => candidate.Id == logoId, cancellationToken);

            if (asset is not null)
            {
                var variants = await _db.AssetVariants.AsNoTracking().Where(variant => variant.AssetId == logoId).ToListAsync(cancellationToken);
                var links = await _delivery.LinksForAsync(asset, variants, cancellationToken);
                preview = (links.FirstOrDefault(link => link.Kind == AssetVariantKind.Medium) ?? (links.Count > 0 ? links[0] : null))?.Url;
            }
        }

        return new SiteThemeResponse(branding.LogoAssetId, preview, branding.PrimaryColor, branding.SecondaryColor);
    }

    private async Task CheckLogoAsync(Guid logoId, FieldErrors errors, CancellationToken cancellationToken)
    {
        var asset = await _db.Assets.AsNoTracking().FirstOrDefaultAsync(candidate => candidate.Id == logoId, cancellationToken);

        if (asset is null)
        {
            errors.Add("logoAssetId", "That logo is not one of your uploads.");
        }
        else if (!asset.IsServable)
        {
            errors.Add("logoAssetId", "Your logo is still being checked. Try again in a minute.");
        }
        else if (!asset.IsImage || asset.Purpose is not (AssetPurpose.AgencyLogo or AssetPurpose.SiteMedia))
        {
            errors.Add("logoAssetId", "Choose an image uploaded as your logo.");
        }
    }

    private static SitePageResponse ToPageResponse(SitePage page) => new(
        page.Id,
        page.Slug,
        page.PageType.ToString(),
        page.Title,
        page.IsSystem,
        page.ShowInNav,
        page.Position,
        page.MetaTitle,
        page.MetaDescription,
        page.Revision,
        page.Blocks
            .OrderBy(block => block.Position)
            .Select(block => SiteBlocks.FromStored(block.BlockType, block.Config) is { } dto ? new SitePageBlockResponse(block.Id, dto) : null)
            .OfType<SitePageBlockResponse>()
            .ToList());

    private static Dictionary<string, string> ReadSocialLinks(IReadOnlyList<SiteSocialLinkDto>? links, FieldErrors errors)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (link, index) in (links ?? []).Select((link, index) => (link, index)))
        {
            var network = link?.Network?.Trim().ToLowerInvariant() ?? string.Empty;

            if (!SiteSnapshots.SocialNetworks.Contains(network, StringComparer.Ordinal))
            {
                errors.Add($"socialLinks[{index}].network", $"Choose one of: {string.Join(", ", SiteSnapshots.SocialNetworks)}.");
                continue;
            }

            if (result.ContainsKey(network))
            {
                errors.Add($"socialLinks[{index}].network", "Add each network once.");
                continue;
            }

            var url = link!.Url?.Trim();

            if (!SiteSnapshots.IsSafeProfileUrl(url))
            {
                errors.Add($"socialLinks[{index}].url", "Use the full https:// address of your profile.");
                continue;
            }

            result[network] = url!;
        }

        return result;
    }

    private static void CheckLength(string? value, int maxLength, string field, FieldErrors errors)
    {
        if (value is not null && value.Trim().Length > maxLength)
        {
            errors.Add(field, $"Keep this to {maxLength} characters or fewer.");
        }
    }

    [GeneratedRegex("^#([0-9A-Fa-f]{3}|[0-9A-Fa-f]{6})$")]
    private static partial Regex HexColourPattern();
}
