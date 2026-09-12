using FluentAssertions;
using TripsAgent.Domain.Catalog;
using TripsAgent.Domain.Common;
using static TripsAgent.UnitTests.Catalog.CatalogSamples;

namespace TripsAgent.UnitTests.Catalog;

/// <summary>
/// Draft → Published → Archived, and what a save does to the rows a product is made of.
/// </summary>
public class ProductLifecycleTests
{
    // ------------------------------------------------------------------ creating

    [Fact]
    public void A_new_product_is_a_draft_holding_exactly_what_was_written()
    {
        var product = Draft();

        product.AgencyId.Should().Be(Agency);
        product.Status.Should().Be(ProductStatus.Draft);
        product.PublishedAt.Should().BeNull();
        product.Slug.Should().Be("zanzibar-escape");

        product.ToContent().Should().BeEquivalentTo(Tour().Normalised(), options => options.WithStrictOrdering());
    }

    [Fact]
    public void Every_row_a_product_is_made_of_carries_the_products_agency()
    {
        var product = Draft();
        var visa = Draft(Visa(), "uk-visa");

        product.Media.Should().OnlyContain(row => row.AgencyId == Agency && row.ProductId == product.Id);
        product.Itinerary.Should().OnlyContain(row => row.AgencyId == Agency && row.ProductId == product.Id);
        product.Inclusions.Should().OnlyContain(row => row.AgencyId == Agency && row.ProductId == product.Id);
        product.PriceVariants.Should().OnlyContain(row => row.AgencyId == Agency && row.ProductId == product.Id);
        visa.Visa!.AgencyId.Should().Be(Agency);
        visa.Visa.Documents.Should().OnlyContain(row => row.AgencyId == Agency && row.VisaDetailsId == visa.Visa.Id);
    }

    [Fact]
    public void Content_that_cannot_be_stored_is_refused_outright()
    {
        var act = () => Product.CreateDraft(Agency, Tour() with { BasePriceMinor = new Money(-1) }, "zanzibar-escape");

        act.Should().Throw<ArgumentException>().WithMessage("*basePriceMinor*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("Zanzibar Escape")]
    [InlineData("-zanzibar")]
    public void A_product_needs_a_well_formed_slug(string slug)
    {
        var act = () => Product.CreateDraft(Agency, Tour(), slug);

        act.Should().Throw<ArgumentException>();
    }

    // ------------------------------------------------------------------ publishing

    [Fact]
    public void Publishing_a_complete_draft_puts_it_live()
    {
        var product = Draft();

        product.TryPublish(Now, Today, out var problems).Should().BeTrue();

        problems.Should().BeEmpty();
        product.Status.Should().Be(ProductStatus.Published);
        product.PublishedAt.Should().Be(Now);
    }

    [Fact]
    public void Publishing_an_incomplete_draft_changes_nothing_and_says_why()
    {
        var product = Draft(Blank(ProductType.Tour), "untitled");
        var version = product.Version;

        product.TryPublish(Now, Today, out var problems).Should().BeFalse();

        Fields(problems).Should().BeEquivalentTo(["title", "basePriceMinor", "media", "availableFrom"]);
        product.Status.Should().Be(ProductStatus.Draft);
        product.PublishedAt.Should().BeNull();
        product.Version.Should().Be(version);
    }

    [Fact]
    public void Only_a_draft_can_be_published()
    {
        var published = Published();
        var archived = Draft();
        archived.Archive();

        FluentActions.Invoking(() => published.TryPublish(Now, Today, out _))
            .Should().Throw<InvalidOperationException>().WithMessage("*already published*");
        FluentActions.Invoking(() => archived.TryPublish(Now, Today, out _))
            .Should().Throw<InvalidOperationException>().WithMessage("*archived*");
    }

    // ------------------------------------------------------------------ unpublishing, archiving

    [Fact]
    public void Unpublishing_takes_a_live_product_back_to_a_draft()
    {
        var product = Published();

        product.Unpublish();

        product.Status.Should().Be(ProductStatus.Draft);
        product.PublishedAt.Should().BeNull();
    }

    [Fact]
    public void Unpublishing_is_also_how_an_archived_product_is_restored()
    {
        var product = Published();
        product.Archive();

        product.Unpublish();

        product.Status.Should().Be(ProductStatus.Draft);
    }

    [Fact]
    public void A_draft_cannot_be_unpublished()
    {
        FluentActions.Invoking(() => Draft().Unpublish())
            .Should().Throw<InvalidOperationException>().WithMessage("*already a draft*");
    }

    [Fact]
    public void A_draft_or_a_live_product_can_be_archived_but_only_once()
    {
        var draft = Draft();
        var live = Published();

        draft.Archive();
        live.Archive();

        draft.Status.Should().Be(ProductStatus.Archived);
        live.Status.Should().Be(ProductStatus.Archived);
        live.PublishedAt.Should().BeNull("an archived product is not on the storefront");

        FluentActions.Invoking(() => draft.Archive())
            .Should().Throw<InvalidOperationException>().WithMessage("*already archived*");
    }

    // ------------------------------------------------------------------ saving

    [Fact]
    public void An_archived_product_cannot_be_changed_until_it_is_restored()
    {
        var product = Draft();
        product.Archive();

        FluentActions.Invoking(() => product.TryRevise(Tour(), "zanzibar-escape", Today, out _))
            .Should().Throw<InvalidOperationException>().WithMessage("*Restore it*");
    }

    [Fact]
    public void A_live_product_cannot_be_saved_into_a_state_it_could_not_be_published_in()
    {
        var product = Published();
        var before = product.ToContent();

        var saved = product.TryRevise(Tour() with { Media = [], HeroAssetId = null, Title = "Renamed" }, "zanzibar-escape", Today, out var problems);

        saved.Should().BeFalse();
        Fields(problems).Should().Equal("media");
        product.ToContent().Should().BeEquivalentTo(before, "a refused edit leaves the live product exactly as it was");
        product.Status.Should().Be(ProductStatus.Published);
    }

    [Fact]
    public void A_live_product_can_be_saved_while_it_stays_publishable()
    {
        var product = Published();

        product.TryRevise(Tour() with { Title = "Zanzibar Escape Deluxe" }, "zanzibar-escape", Today, out var problems)
            .Should().BeTrue();

        problems.Should().BeEmpty();
        product.Title.Should().Be("Zanzibar Escape Deluxe");
        product.Status.Should().Be(ProductStatus.Published);
    }

    [Fact]
    public void A_draft_can_be_saved_with_anything_missing()
    {
        var product = Draft();

        product.TryRevise(Blank(ProductType.Tour), "zanzibar-escape", Today, out var problems).Should().BeTrue();

        problems.Should().BeEmpty();
        product.Media.Should().BeEmpty();
        product.Itinerary.Should().BeEmpty();
        product.PublishProblems(Today).Should().NotBeEmpty();
    }

    [Fact]
    public void Rows_with_a_key_of_their_own_are_updated_in_place_rather_than_replaced()
    {
        var product = Draft();
        var dayTwo = product.Itinerary.Single(day => day.DayNumber == 2).Id;
        var beachRow = product.Media.Single(row => row.AssetId == Beach).Id;
        var tour = Tour();

        product.TryRevise(
            tour with
            {
                // The gallery reordered and a caption changed; day 2 rewritten; day 3 dropped.
                Media = [new ProductMediaContent(StoneTown, "The old town"), new ProductMediaContent(Beach, "Kendwa")],
                Itinerary = [tour.Itinerary[0], tour.Itinerary[1] with { Title = "Spice farm and Jozani forest" }],
            },
            "zanzibar-escape",
            Today,
            out _).Should().BeTrue();

        product.Itinerary.Single(day => day.DayNumber == 2).Id.Should().Be(dayTwo);
        product.Itinerary.Single(day => day.DayNumber == 2).Title.Should().Be("Spice farm and Jozani forest");
        product.Itinerary.Should().HaveCount(2);

        var beach = product.Media.Single(row => row.AssetId == Beach);
        beach.Id.Should().Be(beachRow);
        beach.Position.Should().Be(1);
        beach.Caption.Should().Be("Kendwa");
    }

    [Fact]
    public void Prices_are_replaced_so_an_old_row_can_never_come_back_meaning_something_else()
    {
        var product = Draft();
        var before = product.PriceVariants.Select(variant => variant.Id).ToList();

        product.TryRevise(
            Tour() with { PriceVariants = [new PriceVariantContent("Single room", PaxType.Adult, 1, null, null, Price)] },
            "zanzibar-escape",
            Today,
            out _).Should().BeTrue();

        product.PriceVariants.Should().ContainSingle().Which.Name.Should().Be("Single room");
        product.PriceVariants.Select(variant => variant.Id).Should().NotIntersectWith(before);
    }

    [Fact]
    public void Removing_a_detached_image_takes_it_off_the_product_and_never_touches_the_asset()
    {
        var product = Draft();

        product.TryRevise(
            Tour() with { Media = [new ProductMediaContent(Beach, null)] },
            "zanzibar-escape",
            Today,
            out _).Should().BeTrue();

        product.Media.Should().ContainSingle().Which.AssetId.Should().Be(Beach);
    }

    [Fact]
    public void A_visa_product_can_become_a_tour_and_leaves_its_visa_details_behind()
    {
        var product = Draft(Visa(), "uk-visa");

        product.TryRevise(Tour(), "uk-visa", Today, out _).Should().BeTrue();

        product.ProductType.Should().Be(ProductType.Tour);
        product.Visa.Should().BeNull();
    }

    [Fact]
    public void The_visa_checklist_is_replaced_as_a_whole_in_the_new_order()
    {
        var product = Draft(Visa(), "uk-visa");
        var visa = Visa();

        product.TryRevise(
            visa with
            {
                Visa = visa.Visa! with
                {
                    Documents = [new VisaDocumentContent("Letter of invitation", true), new VisaDocumentContent("Passport", true)],
                },
            },
            "uk-visa",
            Today,
            out _).Should().BeTrue();

        product.ToContent().Visa!.Documents.Select(document => document.Label)
            .Should().Equal("Letter of invitation", "Passport");
    }

    [Fact]
    public void Every_change_moves_the_version_on_so_two_saves_of_the_same_version_cannot_both_win()
    {
        var product = Draft();
        var versions = new List<int> { product.Version };

        product.TryRevise(Tour(), "zanzibar-escape", Today, out _);
        versions.Add(product.Version);
        product.TryPublish(Now, Today, out _);
        versions.Add(product.Version);
        product.Unpublish();
        versions.Add(product.Version);
        product.Archive();
        versions.Add(product.Version);

        versions.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
    }

    [Fact]
    public void The_meals_on_a_day_come_back_in_the_order_they_are_eaten()
    {
        var product = Draft(Tour() with
        {
            Itinerary = [new ItineraryDayContent(1, "Arrival", string.Empty, [Meal.Dinner, Meal.Breakfast, Meal.Lunch], null)],
        });

        product.Itinerary.Single().Meals.Should().Equal(Meal.Breakfast, Meal.Lunch, Meal.Dinner);
    }
}
