using FluentAssertions;
using TripsAgent.Application.Storefront;
using TripsAgent.Contracts.Storefront;
using TripsAgent.Domain.Storefront;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Infrastructure.Identity;

namespace TripsAgent.UnitTests.Storefront;

/// <summary>
/// The snapshot format the storefront renders from, pinned; the starter templates, checked for anything
/// that names the platform; and the signed preview links.
/// </summary>
public class SiteSnapshotAndTemplateTests
{
    private static readonly Guid Hero = Guid.Parse("018f1c2e-0000-7000-8000-000000000001");
    private static readonly Guid Logo = Guid.Parse("018f1c2e-0000-7000-8000-000000000002");

    /// <summary>
    /// The golden copy of a version-1 snapshot. If this test fails because the format changed on purpose,
    /// bump <see cref="SiteSnapshots.SchemaVersion"/>, keep the storefront reading version 1, and update this.
    /// </summary>
    private const string GoldenContent =
        """{"schemaVersion":1,"site":{"name":"Lekki Horizon Travels","language":"en","seoTitle":"Holidays from Lagos","flightSearchEnabled":false},"business":{"contactAddress":"12 Admiralty Way, Lekki, Lagos","email":"hello@lekkihorizon.test","phone":"0803 123 4567","socialLinks":[{"network":"instagram","url":"https://instagram.com/lekkihorizon"}]},"pages":[{"slug":"home","pageType":"Home","title":"Home","showInNav":true,"position":0,"blocks":[{"type":"Hero","hero":{"heading":"Welcome","imageAssetId":"018f1c2e-0000-7000-8000-000000000001","ctaLabel":"Browse","ctaHref":"/tours"}},{"type":"ProductGrid","productGrid":{"heading":"Popular","mode":"Latest","productIds":[],"limit":6}}]},{"slug":"about","pageType":"About","title":"About us","showInNav":true,"position":1,"blocks":[{"type":"Text","text":{"heading":"About us","body":"We plan holidays."}}]}]}""";

    private const string GoldenTheme =
        """{"schemaVersion":1,"templateCode":"horizon","logoAssetId":"018f1c2e-0000-7000-8000-000000000002","primaryColor":"#1F2933","headingFont":"inter","bodyFont":"inter"}""";

    [Fact]
    public void The_snapshot_format_is_exactly_what_the_storefront_expects()
    {
        var (site, branding, pages) = Fixture();

        SiteSnapshots.Serialize(SiteSnapshots.BuildContent(site, branding, pages)).Should().Be(GoldenContent);
        SiteSnapshots.Serialize(SiteSnapshots.BuildTheme("horizon", branding, null)).Should().Be(GoldenTheme);
    }

    [Fact]
    public void A_stored_snapshot_reads_back_to_the_same_content()
    {
        var content = SiteSnapshots.ReadContent(GoldenContent)!;

        SiteSnapshots.Canonical(content).Should().Be(GoldenContent);
        content.Pages[0].Blocks[0].Hero!.ImageAssetId.Should().Be(Hero);
    }

    [Fact]
    public void An_unsafe_social_link_never_reaches_a_snapshot()
    {
        var (_, branding, _) = Fixture();
        branding.SetSocialLinks(new Dictionary<string, string>
        {
            ["instagram"] = "javascript:alert(1)",
            ["myspace"] = "https://myspace.com/x",
            ["facebook"] = "https://facebook.com/lekkihorizon",
        });

        SiteSnapshots.SocialLinksOf(branding).Should().ContainSingle().Which.Network.Should().Be("facebook");
    }

    [Fact]
    public void The_mvp_ships_two_templates_each_with_every_system_page()
    {
        SiteTemplateCatalog.All.Should().HaveCount(2);
        SiteTemplateCatalog.All.Select(template => template.Code).Should().OnlyHaveUniqueItems();

        foreach (var template in SiteTemplateCatalog.All)
        {
            template.Pages.Select(page => page.PageType).Should().BeEquivalentTo(
                nameof(SitePageType.Home), nameof(SitePageType.About), nameof(SitePageType.Catalog),
                nameof(SitePageType.Contact), nameof(SitePageType.Terms));
        }
    }

    [Theory]
    [InlineData("trips")]
    [InlineData("tripsagent")]
    [InlineData("support@")]
    [InlineData("325DEC")]
    public void No_template_copy_names_the_platform(string forbidden)
    {
        // Every word of a template reaches an agency's public site (CLAUDE.md rule 4).
        foreach (var template in SiteTemplateCatalog.All)
        {
            SiteTemplateCatalog.SchemaJson(template).Should().NotContainEquivalentOf(forbidden, $"template {template.Code}");
            template.Description.Should().NotContainEquivalentOf(forbidden);
        }
    }

    [Fact]
    public void Every_template_block_validates_once_the_agency_name_is_written_in()
    {
        foreach (var template in SiteTemplateCatalog.All)
        {
            foreach (var page in template.Pages)
            {
                var errors = new FieldErrors();
                SiteBlockValidator.Validate(
                    page.Blocks.Select(block => (SiteBlockDto?)SiteTemplateCatalog.Personalise(block, new string('N', 40))).ToList(),
                    "blocks",
                    errors);

                errors.Any.Should().BeFalse($"{template.Code}/{page.Slug}: {string.Join("; ", errors.ToDictionary().SelectMany(pair => pair.Value))}");
            }
        }
    }

    [Fact]
    public void The_terms_page_is_a_marked_placeholder_not_legal_wording() =>
        SiteTemplateCatalog.All
            .SelectMany(template => template.Pages)
            .Where(page => page.PageType == nameof(SitePageType.Terms))
            .Should().OnlyContain(page => page.Blocks.Any(block => block.Text != null && block.Text.Body.StartsWith("Replace this text", StringComparison.Ordinal)));

    [Fact]
    public void A_preview_link_opens_one_version_until_it_expires()
    {
        var clock = new StorefrontTestClock(new DateTimeOffset(2026, 9, 11, 9, 0, 0, TimeSpan.Zero));
        var tokens = new SitePreviewTokens(new HmacTokenHasher(new byte[32]), clock, new StorefrontOptions());
        var (agency, site, version) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());

        var (token, expiresAt) = tokens.Issue(agency, site, version);

        tokens.Read(token).Should().Be(new SitePreviewGrant(agency, site, version, expiresAt));
        expiresAt.Should().Be(clock.GetUtcNow().AddHours(12));

        var forged = token.Replace(version.ToString("N"), Guid.CreateVersion7().ToString("N"), StringComparison.Ordinal);
        tokens.Read(forged).Should().BeNull("a changed version id breaks the signature");

        clock.Now = expiresAt;
        tokens.Read(token).Should().BeNull("an expired link opens nothing");
    }

    [Fact]
    public void A_preview_link_never_lasts_more_than_a_day()
    {
        var clock = new StorefrontTestClock(new DateTimeOffset(2026, 9, 11, 9, 0, 0, TimeSpan.Zero));
        var tokens = new SitePreviewTokens(
            new HmacTokenHasher(new byte[32]),
            clock,
            new StorefrontOptions { PreviewLinkLifetime = TimeSpan.FromDays(30) });

        tokens.Issue(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7()).ExpiresAt
            .Should().Be(clock.GetUtcNow().AddDays(1));
    }

    private static (Site Site, AgencyBranding Branding, List<SitePage> Pages) Fixture()
    {
        var agency = Agency.RegisterPrincipal("Lekki Horizon Travels Limited", "lekki-horizon", "NG", "NGN", "Africa/Lagos");
        var branding = AgencyBranding.CreateDefault(agency);
        branding.SetLogo(Logo);
        branding.SetContactAddress("12 Admiralty Way, Lekki, Lagos");
        branding.SetContactDetails("hello@lekkihorizon.test", "0803 123 4567", null);
        branding.SetSocialLinks(new Dictionary<string, string> { ["instagram"] = "https://instagram.com/lekkihorizon" });

        var site = Site.Create(agency.Id, Guid.CreateVersion7(), "Lekki Horizon Travels");
        site.UpdateSettings("Lekki Horizon Travels", "Holidays from Lagos", null, flightSearchEnabled: false);
        var draft = SiteVersion.CreateDraft(site);

        var home = SitePage.Create(draft, SitePageType.Home, "home", "Home", true, 0);
        home.ReplaceBlocks(
        [
            Stored(SiteBlocks.Hero(new HeroBlockConfig("Welcome", null, Hero, "Browse", "/tours"))),
            Stored(SiteBlocks.ProductGrid(new ProductGridBlockConfig("Popular", "Latest", null, [], 6))),
        ]);

        var about = SitePage.Create(draft, SitePageType.About, "about", "About us", true, 1);
        about.ReplaceBlocks([Stored(SiteBlocks.Text(new TextBlockConfig("About us", "We plan holidays.")))]);

        // Out of order on purpose: the snapshot orders pages by position, whatever order they load in.
        return (site, branding, [about, home]);
    }

    private static SiteBlockContent Stored(SiteBlockDto block) =>
        new(null, Enum.Parse<SiteBlockType>(block.Type), SiteBlocks.ToStored(block));
}

/// <summary>A clock a test can move.</summary>
internal sealed class StorefrontTestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
