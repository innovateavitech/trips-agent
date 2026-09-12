using FluentAssertions;
using TripsAgent.Application.Storefront;
using TripsAgent.Contracts.Storefront;
using TripsAgent.Domain.Storefront;

namespace TripsAgent.UnitTests.Storefront;

/// <summary>
/// Page addresses, whole-page block saves, and the rules each block's settings follow. A block that
/// passes here renders; one that does not never reaches the database.
/// </summary>
public class SitePageAndBlockTests
{
    private static readonly Guid Image = Guid.Parse("018f1c2e-0000-7000-8000-000000000001");

    [Theory]
    [InlineData("Group-Trips", "group-trips")]
    [InlineData("/visas/", "visas")]
    public void A_slug_is_normalised(string input, string expected)
    {
        SitePageRules.TryNormaliseSlug(input, out var slug, out _).Should().BeTrue();
        slug.Should().Be(expected);
    }

    [Theory]
    [InlineData("home")]
    [InlineData("preview")]
    [InlineData("media")]
    [InlineData("two  words")]
    [InlineData("-leading")]
    public void A_slug_the_site_needs_or_cannot_use_is_refused(string input) =>
        SitePageRules.TryNormaliseSlug(input, out _, out _).Should().BeFalse();

    [Theory]
    [InlineData("Group Holidays & Tours!", "group-holidays-tours")]
    [InlineData("Café culture", "cafe-culture")]
    [InlineData("", "page")]
    [InlineData("Preview", "page")]
    public void A_slug_is_made_from_a_title(string title, string expected) =>
        SitePageRules.SlugFrom(title).Should().Be(expected);

    [Fact]
    public void Saving_blocks_rewrites_positions_keeps_known_blocks_and_drops_the_rest()
    {
        var page = NewPage(SitePageType.Custom, "offers");
        page.ReplaceBlocks([Content(null, "First"), Content(null, "Second"), Content(null, "Third")]);

        var first = page.Blocks[0];
        var third = page.Blocks[2];
        var revision = page.Revision;

        // Third moves to the top, first stays, second goes, and one new block arrives.
        page.ReplaceBlocks([Content(third.Id, "Third, edited"), Content(first.Id, "First"), Content(null, "New")]);

        page.Blocks.Select(block => block.Position).Should().Equal(0, 1, 2);
        page.Blocks[0].Id.Should().Be(third.Id);
        page.Blocks[1].Id.Should().Be(first.Id);
        page.Blocks[0].Config.Should().Contain("Third, edited");
        page.Blocks.Should().HaveCount(3);
        page.Revision.Should().Be(revision + 1);
    }

    [Fact]
    public void A_block_whose_type_changed_becomes_a_new_block()
    {
        var page = NewPage(SitePageType.Custom, "offers");
        page.ReplaceBlocks([Content(null, "Text")]);
        var original = page.Blocks[0].Id;

        page.ReplaceBlocks([new SiteBlockContent(original, SiteBlockType.Contact, """{"heading":"Talk to us"}""")]);

        page.Blocks.Should().ContainSingle().Which.Id.Should().NotBe(original);
    }

    [Fact]
    public void The_home_page_keeps_its_address_and_its_place_in_the_menu()
    {
        var page = NewPage(SitePageType.Home, "ignored");

        page.UpdateDetails("Welcome", "somewhere-else", showInNav: false, null, null);

        page.Slug.Should().Be(SitePageRules.HomeSlug);
        page.ShowInNav.Should().BeTrue();
    }

    [Fact]
    public void A_valid_page_of_every_block_type_passes_and_names_what_it_points_at()
    {
        var errors = new FieldErrors();
        var product = Guid.CreateVersion7();

        var result = SiteBlockValidator.Validate(
        [
            SiteBlocks.Hero(new HeroBlockConfig("Welcome", null, Image, "Browse", "/tours")),
            SiteBlocks.ProductGrid(new ProductGridBlockConfig("Picked for you", "selected", "visa", [product, product], 3)),
            SiteBlocks.Text(new TextBlockConfig(null, "  Plain words.  ")),
            SiteBlocks.Contact(new ContactBlockConfig("Talk to us", null, true, true, false, true)),
        ], "blocks", errors);

        errors.Any.Should().BeFalse(string.Join("; ", errors.ToDictionary().SelectMany(pair => pair.Value)));
        result.Blocks.Should().HaveCount(4);
        result.Images.Should().ContainSingle().Which.Should().Be((Image, "blocks[0].hero.imageAssetId"));
        result.Products.Should().ContainSingle("a product chosen twice is shown once").Which.ProductId.Should().Be(product);

        var grid = result.Blocks[1].Block.ProductGrid!;
        grid.Mode.Should().Be("Selected");
        grid.ProductType.Should().Be("Visa");
        result.Blocks[2].Block.Text!.Body.Should().Be("Plain words.");
    }

    [Fact]
    public void Every_problem_is_reported_against_the_field_that_caused_it()
    {
        var errors = new FieldErrors();

        SiteBlockValidator.Validate(
        [
            SiteBlocks.Hero(new HeroBlockConfig(" ", null, null, "Go", null)),
            new SiteBlockDto("Gallery", null, null, null, null),
            SiteBlocks.ProductGrid(new ProductGridBlockConfig("Picks", "Selected", "5", [], 0)),
            new SiteBlockDto("Text", null, null, new TextBlockConfig(null, "x"), new ContactBlockConfig("y", null, true, true, true, true)),
        ], "blocks", errors);

        errors.ToDictionary().Keys.Should().BeEquivalentTo(
            "blocks[0].hero.heading",
            "blocks[0].hero.ctaHref",
            "blocks[1].type",
            "blocks[2].productGrid.productType",
            "blocks[2].productGrid.productIds",
            "blocks[2].productGrid.limit",
            "blocks[3]");
    }

    [Fact]
    public void The_newest_products_mode_carries_no_list_of_ids()
    {
        var errors = new FieldErrors();

        var result = SiteBlockValidator.Validate(
            [SiteBlocks.ProductGrid(new ProductGridBlockConfig("Popular", "Latest", null, [Guid.CreateVersion7()], 6))],
            "blocks",
            errors);

        result.Blocks[0].Block.ProductGrid!.ProductIds.Should().BeEmpty();
        result.Products.Should().BeEmpty();
    }

    [Theory]
    [InlineData("/contact", true)]
    [InlineData("/tours?type=visa#top", true)]
    [InlineData("https://wa.me/2348031234567", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("//evil.example.com", false)]
    [InlineData("http://example.com", false)]
    [InlineData("https://user:pass@example.com", false)]
    [InlineData("/<script>", false)]
    public void Only_a_site_path_or_an_https_address_is_a_safe_link(string href, bool safe) =>
        SiteBlockValidator.IsSafeLink(href).Should().Be(safe);

    [Fact]
    public void A_stored_block_reads_back_as_it_was_written()
    {
        var block = SiteBlocks.Hero(new HeroBlockConfig("Welcome", "Sub", Image, "Browse", "/tours"));

        SiteBlocks.FromStored(SiteBlockType.Hero, SiteBlocks.ToStored(block)).Should().BeEquivalentTo(block);
        SiteBlocks.FromStored(SiteBlockType.Hero, "not json").Should().BeNull("a block that cannot be read is skipped, not fatal");
    }

    private static SitePage NewPage(SitePageType type, string slug)
    {
        var site = Site.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), "Lekki Horizon Travels");
        return SitePage.Create(SiteVersion.CreateDraft(site), type, slug, "A page", showInNav: true, position: 1);
    }

    private static SiteBlockContent Content(Guid? id, string body) =>
        new(id, SiteBlockType.Text, SiteBlocks.ToStored(SiteBlocks.Text(new TextBlockConfig(null, body))));
}
