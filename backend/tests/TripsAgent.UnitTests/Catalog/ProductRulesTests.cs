using FluentAssertions;
using TripsAgent.Domain.Catalog;
using TripsAgent.Domain.Common;
using static TripsAgent.UnitTests.Catalog.CatalogSamples;

namespace TripsAgent.UnitTests.Catalog;

/// <summary>
/// What can be <i>stored</i>. Lenient about what is not written yet, strict about what can never be
/// right — and it says everything wrong at once.
/// </summary>
public class ProductRulesTests
{
    [Fact]
    public void A_complete_product_can_be_saved()
    {
        ProductRules.Validate(Tour()).Should().BeEmpty();
        ProductRules.Validate(Visa()).Should().BeEmpty();
    }

    [Theory]
    [InlineData(ProductType.Tour)]
    [InlineData(ProductType.Package)]
    [InlineData(ProductType.Visa)]
    public void A_draft_with_nothing_in_it_can_be_saved(ProductType type)
    {
        // Saving a draft never enforces the publish rules — the agent is still writing it.
        ProductRules.Validate(Blank(type)).Should().BeEmpty();
    }

    [Fact]
    public void An_unknown_product_type_is_refused()
    {
        Fields(ProductRules.Validate(Tour() with { ProductType = default })).Should().Contain("productType");
    }

    [Theory]
    [InlineData(new[] { 1, 3 }, "itinerary[1].dayNumber")]
    [InlineData(new[] { 2 }, "itinerary[0].dayNumber")]
    [InlineData(new[] { 1, 1 }, "itinerary[1].dayNumber")]
    [InlineData(new[] { 2, 1 }, "itinerary[0].dayNumber")]
    public void Days_run_from_one_in_order_with_no_gaps_or_repeats(int[] dayNumbers, string field)
    {
        var itinerary = dayNumbers
            .Select(number => new ItineraryDayContent(number, $"Day {number}", string.Empty, [], null))
            .ToList();

        var problems = ProductRules.Validate(Tour() with { Itinerary = itinerary });

        Fields(problems).Should().Contain(field);
        problems.First(problem => problem.Field == field).Message.Should().Contain("1, 2, 3");
    }

    [Fact]
    public void A_visa_has_no_itinerary_and_no_inclusions()
    {
        var tour = Tour();

        var problems = ProductRules.Validate(Visa() with { Itinerary = tour.Itinerary, Inclusions = tour.Inclusions });

        Fields(problems).Should().BeEquivalentTo(["itinerary", "inclusions"]);
    }

    [Theory]
    [InlineData(ProductType.Tour)]
    [InlineData(ProductType.Package)]
    public void Visa_details_only_go_on_a_visa(ProductType type)
    {
        var problems = ProductRules.Validate(Tour() with { ProductType = type, Visa = Visa().Visa });

        Fields(problems).Should().Equal("visa");
    }

    [Fact]
    public void The_cover_image_must_be_one_of_the_gallery_images()
    {
        var problems = ProductRules.Validate(Tour() with { HeroAssetId = Guid.CreateVersion7() });

        Fields(problems).Should().Equal("heroAssetId");
    }

    [Fact]
    public void An_image_can_only_be_in_the_gallery_once()
    {
        var problems = ProductRules.Validate(Tour() with
        {
            Media = [new ProductMediaContent(Beach, null), new ProductMediaContent(Beach, "again")],
        });

        Fields(problems).Should().Equal("media[1].assetId");
    }

    [Fact]
    public void No_price_anywhere_may_be_negative()
    {
        var tour = Tour();
        var visa = Visa();

        Fields(ProductRules.Validate(tour with { BasePriceMinor = new Money(-1) })).Should().Equal("basePriceMinor");

        Fields(ProductRules.Validate(tour with
        {
            PriceVariants = [tour.PriceVariants[0] with { PriceMinor = new Money(-1) }],
        })).Should().Equal("priceVariants[0].priceMinor");

        Fields(ProductRules.Validate(visa with
        {
            Visa = visa.Visa! with { ConsularFeeMinor = new Money(-1), ServiceFeeMinor = new Money(-1) },
        })).Should().BeEquivalentTo(["visa.consularFeeMinor", "visa.serviceFeeMinor"]);
    }

    [Fact]
    public void A_price_beyond_the_pricing_ceiling_is_refused()
    {
        var problems = ProductRules.Validate(Tour() with { BasePriceMinor = new Money(CatalogLimits.MaxPriceMinor + 1) });

        Fields(problems).Should().Equal("basePriceMinor");
    }

    [Fact]
    public void A_group_size_range_must_run_forwards()
    {
        var problems = ProductRules.Validate(Tour() with
        {
            PriceVariants = [new PriceVariantContent("Group", PaxType.Adult, null, 10, 2, Price)],
        });

        Fields(problems).Should().Equal("priceVariants[0].maxGroupSize");
    }

    [Fact]
    public void Two_prices_a_traveller_would_match_at_once_are_refused()
    {
        var problems = ProductRules.Validate(Tour() with
        {
            PriceVariants =
            [
                new PriceVariantContent("Group rate", PaxType.Adult, null, 2, 6, Price),
                new PriceVariantContent("group rate", PaxType.Adult, null, 5, 10, Price),
            ],
        });

        Fields(problems).Should().Equal("priceVariants[1]");
    }

    [Fact]
    public void An_open_ended_price_overlaps_everything_on_its_side()
    {
        var problems = ProductRules.Validate(Tour() with
        {
            PriceVariants =
            [
                new PriceVariantContent("Adult", PaxType.Adult, null, null, null, Price),
                new PriceVariantContent("Adult", PaxType.Adult, null, 4, null, Price),
            ],
        });

        Fields(problems).Should().Equal("priceVariants[1]");
    }

    [Fact]
    public void Prices_for_different_travellers_rooms_or_group_sizes_do_not_clash()
    {
        var problems = ProductRules.Validate(Tour() with
        {
            PriceVariants =
            [
                new PriceVariantContent("Standard", PaxType.Adult, 2, 1, 4, Price),
                new PriceVariantContent("Standard", PaxType.Adult, 2, 5, 10, Price),
                new PriceVariantContent("Standard", PaxType.Adult, 1, 1, 4, Price),
                new PriceVariantContent("Standard", PaxType.Child, 2, 1, 4, Price),
            ],
        });

        problems.Should().BeEmpty();
    }

    [Fact]
    public void A_blank_line_of_text_is_refused_wherever_it_is()
    {
        var tour = Tour();
        var visa = Visa();

        Fields(ProductRules.Validate(tour with
        {
            Inclusions = [new InclusionContent(InclusionKind.Inclusion, " ")],
        })).Should().Equal("inclusions[0].text");

        Fields(ProductRules.Validate(tour with
        {
            PriceVariants = [tour.PriceVariants[0] with { Name = string.Empty }],
        })).Should().Equal("priceVariants[0].name");

        Fields(ProductRules.Validate(visa with
        {
            Visa = visa.Visa! with { Documents = [new VisaDocumentContent(string.Empty, true)] },
        })).Should().Equal("visa.documents[0].label");
    }

    [Theory]
    [InlineData("Kenya")]
    [InlineData("ke")]
    [InlineData("K")]
    public void A_destination_country_is_a_two_letter_code(string country)
    {
        Fields(ProductRules.Validate(Tour() with { DestinationCountry = country })).Should().Equal("destinationCountry");
    }

    [Theory]
    [InlineData("")]
    [InlineData("naira")]
    public void A_currency_is_a_three_letter_code(string currency)
    {
        Fields(ProductRules.Validate(Tour() with { Currency = currency })).Should().Equal("currency");
    }

    [Fact]
    public void A_booking_window_must_run_forwards()
    {
        var problems = ProductRules.Validate(Tour() with { AvailableFrom = Today.AddDays(5), AvailableTo = Today });

        Fields(problems).Should().Equal("availableTo");
    }

    [Theory]
    [InlineData(-1, "visa.processingTimeDays")]
    [InlineData(366, "visa.processingTimeDays")]
    public void Visa_processing_time_is_a_sensible_number_of_days(int days, string field)
    {
        var visa = Visa();

        Fields(ProductRules.Validate(visa with { Visa = visa.Visa! with { ProcessingTimeDays = days } }))
            .Should().Equal(field);
    }

    [Fact]
    public void Every_problem_is_reported_not_just_the_first()
    {
        var tour = Tour();

        var problems = ProductRules.Validate(tour with
        {
            DestinationCountry = "Kenya",
            DurationDays = 0,
            BasePriceMinor = new Money(-5),
            HeroAssetId = Guid.CreateVersion7(),
            Itinerary = [tour.Itinerary[1]],
            Inclusions = [new InclusionContent(default, string.Empty)],
        });

        Fields(problems).Should().BeEquivalentTo(
        [
            "destinationCountry",
            "durationDays",
            "basePriceMinor",
            "heroAssetId",
            "itinerary[0].dayNumber",
            "inclusions[0].kind",
            "inclusions[0].text",
        ]);
    }

    [Fact]
    public void Normalising_tidies_what_the_agent_typed_without_reordering_anything()
    {
        var tour = Tour();

        var tidy = (tour with
        {
            Title = "  Zanzibar Escape ",
            DestinationCountry = " tz ",
            DestinationCity = "   ",
            Currency = "ngn",
            Itinerary = [tour.Itinerary[0] with { Meals = [Meal.Dinner, Meal.Breakfast, Meal.Dinner] }],
        }).Normalised();

        tidy.Title.Should().Be("Zanzibar Escape");
        tidy.DestinationCountry.Should().Be("TZ");
        tidy.DestinationCity.Should().BeNull("blank optional text is no text");
        tidy.Currency.Should().Be("NGN");
        tidy.Itinerary[0].Meals.Should().Equal(Meal.Breakfast, Meal.Dinner);
        tidy.Media.Select(item => item.AssetId).Should().Equal(Beach, StoneTown);
    }
}
