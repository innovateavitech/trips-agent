using System.Text.Json;
using TripsAgent.Contracts.Storefront;
using TripsAgent.Domain.Storefront;

namespace TripsAgent.Application.Storefront;

/// <summary>A starter website: its layout code, and the pages a site made from it begins with.</summary>
public sealed record SiteTemplateDefinition(
    string Code,
    string Name,
    string Description,
    int Version,
    IReadOnlyList<SiteTemplatePageDefinition> Pages);

/// <summary>One page of a template, with its blocks in order.</summary>
/// <param name="PageType">A <see cref="SitePageType"/> name.</param>
public sealed record SiteTemplatePageDefinition(
    string PageType,
    string Slug,
    string Title,
    bool ShowInNav,
    IReadOnlyList<SiteBlockDto> Blocks);

/// <summary>What <c>site_templates.block_schema</c> holds.</summary>
public sealed record SiteTemplateSchema(IReadOnlyList<SiteTemplatePageDefinition> Pages);

/// <summary>
/// The two starter websites the MVP ships (docs/BUILD_PLAN.md), seeded into <c>storefront.site_templates</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every word here ends up on an agency's public site, so none of it may name the platform, its domain
/// or its support address (CLAUDE.md rule 4) — a test reads every template for them. The copy is
/// written for the agent to replace, and <see cref="BusinessNamePlaceholder"/> becomes the agency's own
/// name when a site is created.
/// </para>
/// <para>
/// The terms page is a clearly marked placeholder, not legal wording: terms are the agency's to write.
/// </para>
/// </remarks>
public static class SiteTemplateCatalog
{
    public const string BusinessNamePlaceholder = "{{businessName}}";

    /// <summary>Where the catalog page lives until the agent renames it.</summary>
    public const string CatalogSlug = "tours";

    public static IReadOnlyList<SiteTemplateDefinition> All { get; } = [Horizon(), Harbour()];

    /// <summary>
    /// The layout to fall back on when a site's own template cannot be read — a site created before
    /// a template was withdrawn, say. The storefront must render something rather than nothing.
    /// </summary>
    public static string DefaultCode => All[0].Code;

    /// <summary>The JSON stored in <c>site_templates.block_schema</c>.</summary>
    public static string SchemaJson(SiteTemplateDefinition template)
    {
        ArgumentNullException.ThrowIfNull(template);

        return JsonSerializer.Serialize(new SiteTemplateSchema(template.Pages), SiteBlocks.Json);
    }

    /// <summary>A stored schema, read back. Throws when it cannot be read: a broken template is a bug.</summary>
    public static SiteTemplateSchema ReadSchema(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        return JsonSerializer.Deserialize<SiteTemplateSchema>(json, SiteBlocks.Json)
               ?? throw new InvalidOperationException("A site template's schema is empty.");
    }

    /// <summary>A template block with the agency's name written in.</summary>
    public static SiteBlockDto Personalise(SiteBlockDto block, string businessName)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentException.ThrowIfNullOrWhiteSpace(businessName);

        string? Fill(string? text) => text?.Replace(BusinessNamePlaceholder, businessName, StringComparison.Ordinal);

        return block with
        {
            Hero = block.Hero is { } hero ? hero with { Heading = Fill(hero.Heading)!, Subheading = Fill(hero.Subheading) } : null,
            ProductGrid = block.ProductGrid is { } grid ? grid with { Heading = Fill(grid.Heading)! } : null,
            Text = block.Text is { } text ? text with { Heading = Fill(text.Heading), Body = Fill(text.Body)! } : null,
            Contact = block.Contact is { } contact ? contact with { Heading = Fill(contact.Heading)!, Intro = Fill(contact.Intro) } : null,
        };
    }

    private static SiteTemplateDefinition Horizon() => new(
        "horizon",
        "Horizon",
        "Bold and image-led: a full-width banner, then your newest products and a way to get in touch.",
        1,
        [
            Page(SitePageType.Home, SitePageRules.HomeSlug, "Home",
            [
                SiteBlocks.Hero(new HeroBlockConfig(
                    $"Your next journey starts with {BusinessNamePlaceholder}",
                    "Tours, holidays and visa support, planned by people who know the way. Tell us where you want to go and we will take care of the rest.",
                    null,
                    "Browse our tours",
                    $"/{CatalogSlug}")),
                SiteBlocks.ProductGrid(new ProductGridBlockConfig("Popular right now", SiteBlocks.LatestMode, null, [], 6)),
                SiteBlocks.Text(new TextBlockConfig(
                    "Why travel with us",
                    "We plan every journey as if we were taking it ourselves: clear prices, honest advice and someone to call when you need us.\n\n"
                    + "From weekend getaways to once-in-a-lifetime holidays, we handle the details so you can enjoy the journey.")),
                SiteBlocks.Contact(new ContactBlockConfig("Talk to us", "Call, message or visit. We are happy to help you plan.", true, true, true, true)),
            ]),
            About("About us"),
            Catalog("Tours and packages"),
            Contact("Get in touch"),
            Terms(),
        ]);

    private static SiteTemplateDefinition Harbour() => new(
        "harbour",
        "Harbour",
        "Calm and simple: a short welcome beside a picture, a few chosen products, and your story.",
        1,
        [
            Page(SitePageType.Home, SitePageRules.HomeSlug, "Home",
            [
                SiteBlocks.Hero(new HeroBlockConfig(
                    "Holidays, made easy",
                    $"{BusinessNamePlaceholder} plans holidays, tours and visas for travellers who want it done right.",
                    null,
                    "See where we go",
                    $"/{CatalogSlug}")),
                SiteBlocks.Text(new TextBlockConfig(
                    "How we work",
                    "Tell us where and when. We will suggest options that fit your budget, confirm every detail, and stay with you until you are home again.")),
                SiteBlocks.ProductGrid(new ProductGridBlockConfig("Holidays we love", SiteBlocks.LatestMode, null, [], 3)),
                SiteBlocks.Contact(new ContactBlockConfig("Plan with us", "Send us a message and we will get back to you.", true, true, true, false)),
            ]),
            About("Our story"),
            Catalog("Holidays"),
            Contact("Contact us"),
            Terms(),
        ]);

    private static SiteTemplatePageDefinition About(string title) =>
        Page(SitePageType.About, "about", title,
        [
            SiteBlocks.Text(new TextBlockConfig(
                $"About {BusinessNamePlaceholder}",
                $"{BusinessNamePlaceholder} helps travellers plan and book holidays with confidence.\n\n"
                + "Replace this with your own story: who you are, how long you have been in business, and what makes the holidays you plan different.")),
        ]);

    private static SiteTemplatePageDefinition Catalog(string title) =>
        Page(SitePageType.Catalog, CatalogSlug, title,
        [
            SiteBlocks.Text(new TextBlockConfig(
                null,
                "Browse the tours, packages and visas we offer. See something you like? Get in touch and we will help you book.")),
        ]);

    private static SiteTemplatePageDefinition Contact(string title) =>
        Page(SitePageType.Contact, "contact", title,
        [
            SiteBlocks.Contact(new ContactBlockConfig(title, "We would love to hear about the holiday you have in mind.", true, true, true, true)),
        ]);

    private static SiteTemplatePageDefinition Terms() =>
        Page(SitePageType.Terms, "terms", "Terms and conditions",
        [
            SiteBlocks.Text(new TextBlockConfig(
                "Terms and conditions",
                "Replace this text with your own terms and conditions before you publish your site.\n\n"
                + "Your terms should explain how bookings are confirmed, how payment works, and your cancellation and refund policy. "
                + "If you are unsure what to include, ask a legal adviser.")),
        ],
        showInNav: false);

    private static SiteTemplatePageDefinition Page(
        SitePageType type,
        string slug,
        string title,
        IReadOnlyList<SiteBlockDto> blocks,
        bool showInNav = true) =>
        new(type.ToString(), slug, title, showInNav, blocks);
}
