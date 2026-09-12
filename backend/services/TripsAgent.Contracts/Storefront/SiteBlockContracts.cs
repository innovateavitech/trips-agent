namespace TripsAgent.Contracts.Storefront;

/// <summary>
/// The block types a page is built from, by the name the API and the published snapshot use.
/// </summary>
/// <remarks>
/// One list for the builder in the console and the renderer in the storefront, both generated from
/// these records by <c>pnpm generate:api</c>. The MVP ships four (docs/BUILD_PLAN.md). A block type the
/// renderer does not know is skipped, not fatal, so an old snapshot and a newer renderer — or the other
/// way round — never take a site down.
/// </remarks>
public static class SiteBlockTypes
{
    public const string Hero = "Hero";
    public const string ProductGrid = "ProductGrid";
    public const string Text = "Text";
    public const string Contact = "Contact";

    public static IReadOnlyList<string> All { get; } = [Hero, ProductGrid, Text, Contact];
}

/// <summary>
/// One block: its type, and the settings for that type. Exactly one of the settings is filled — the
/// one <see cref="Type"/> names.
/// </summary>
/// <remarks>
/// A property per type rather than one untyped <c>config</c> object, so the console and the storefront
/// both get a real TypeScript type for every block, and a misspelled field is a compile error rather
/// than a block that silently renders empty.
/// </remarks>
public sealed record SiteBlockDto(
    string Type,
    HeroBlockConfig? Hero,
    ProductGridBlockConfig? ProductGrid,
    TextBlockConfig? Text,
    ContactBlockConfig? Contact);

/// <summary>The banner at the top of a page.</summary>
/// <param name="ImageAssetId">An uploaded, scanned image. Never a URL: an asset id is the only way in.</param>
/// <param name="CtaHref">A path on the site, like <c>/contact</c>, or an <c>https://</c> address.</param>
public sealed record HeroBlockConfig(
    string Heading,
    string? Subheading,
    Guid? ImageAssetId,
    string? CtaLabel,
    string? CtaHref);

/// <summary>
/// A grid of the agency's products. Holds a selection, never a price: prices come from the catalog when
/// the page is rendered, so this block can never show a stale figure or a net rate.
/// </summary>
/// <param name="Mode"><c>Latest</c> (newest published first) or <c>Selected</c> (the ids given, in order).</param>
/// <param name="ProductType">Only this kind — <c>Tour</c>, <c>Package</c> or <c>Visa</c> — or null for all.</param>
/// <param name="ProductIds">For <c>Selected</c>. A product that is later unpublished simply drops out.</param>
/// <param name="Limit">How many to show, 1 to 12.</param>
public sealed record ProductGridBlockConfig(
    string Heading,
    string Mode,
    string? ProductType,
    IReadOnlyList<Guid> ProductIds,
    int Limit);

/// <summary>
/// A heading and paragraphs of plain text, split on blank lines.
/// </summary>
/// <remarks>
/// Plain text, not HTML, on purpose: it is shown on the agent's own public domain, and text that can
/// never be markup can never carry a script to their customers.
/// </remarks>
public sealed record TextBlockConfig(string? Heading, string Body);

/// <summary>
/// How to reach the agency. Display settings only: the details themselves come from the agency's
/// branding, so they are written once and shown everywhere. Messages sent through the site arrive with
/// the CRM work (issue 62).
/// </summary>
public sealed record ContactBlockConfig(
    string Heading,
    string? Intro,
    bool ShowEmail,
    bool ShowPhone,
    bool ShowWhatsApp,
    bool ShowAddress);
