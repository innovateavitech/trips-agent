using FluentAssertions;
using TripsAgent.Domain.Catalog;
using TripsAgent.Domain.Common;
using static TripsAgent.UnitTests.Catalog.CatalogSamples;

namespace TripsAgent.UnitTests.Catalog;

/// <summary>
/// "Why can't I publish?" — each rule from issue #160 on its own, and all of them at once. No
/// database: the rules are the domain's, and so are these tests.
/// </summary>
public class ProductPublishRulesTests
{
    [Fact]
    public void A_complete_tour_can_be_published()
    {
        ProductPublishRules.Check(Tour(), Today).Should().BeEmpty();
    }

    [Fact]
    public void A_complete_visa_can_be_published()
    {
        ProductPublishRules.Check(Visa(), Today).Should().BeEmpty();
    }

    [Fact]
    public void A_blank_draft_is_told_everything_it_is_missing_at_once()
    {
        var problems = ProductPublishRules.Check(Blank(ProductType.Tour), Today);

        Fields(problems).Should().BeEquivalentTo(
            ["title", "basePriceMinor", "media", "availableFrom"],
            "every problem, never just the first, so the checklist is complete in one read");
    }

    [Fact]
    public void A_title_is_needed()
    {
        var problems = ProductPublishRules.Check(Tour() with { Title = "   " }, Today);

        Fields(problems).Should().Equal("title");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_price_above_zero_is_needed(long priceMinor)
    {
        var problems = ProductPublishRules.Check(Tour() with { BasePriceMinor = new Money(priceMinor) }, Today);

        Fields(problems).Should().Equal("basePriceMinor");
    }

    [Fact]
    public void At_least_one_image_is_needed()
    {
        var problems = ProductPublishRules.Check(Tour() with { Media = [], HeroAssetId = null }, Today);

        Fields(problems).Should().Equal("media");
    }

    [Theory]
    [InlineData(ProductType.Tour)]
    [InlineData(ProductType.Package)]
    public void A_tour_or_package_needs_a_booking_window(ProductType type)
    {
        var problems = ProductPublishRules.Check(
            Tour() with { ProductType = type, AvailableFrom = null, AvailableTo = null },
            Today);

        Fields(problems).Should().Equal("availableFrom");
    }

    [Fact]
    public void Either_end_of_the_window_is_enough()
    {
        ProductPublishRules.Check(Tour() with { AvailableTo = null }, Today).Should().BeEmpty("open-ended from a date");
        ProductPublishRules.Check(Tour() with { AvailableFrom = null }, Today).Should().BeEmpty("bookable from now until a date");
    }

    [Fact]
    public void A_window_that_ended_yesterday_cannot_be_published_and_says_when_it_closed()
    {
        var problems = ProductPublishRules.Check(
            Tour() with { AvailableFrom = new DateOnly(2026, 8, 1), AvailableTo = new DateOnly(2026, 9, 10) },
            Today);

        problems.Should().ContainSingle();
        problems[0].Field.Should().Be("availableTo");
        problems[0].Message.Should().Contain("10 September 2026");
    }

    [Fact]
    public void A_window_that_ends_today_is_still_open_today()
    {
        ProductPublishRules.Check(Tour() with { AvailableTo = Today }, Today).Should().BeEmpty();
    }

    [Fact]
    public void A_window_that_has_not_started_yet_can_still_be_published()
    {
        // Published now, bookable for the dates it offers: the storefront can take an early booking.
        ProductPublishRules.Check(Tour() with { AvailableFrom = Today.AddYears(1), AvailableTo = null }, Today)
            .Should().BeEmpty();
    }

    [Fact]
    public void A_visa_needs_no_booking_window()
    {
        var visa = Visa() with { AvailableFrom = null, AvailableTo = null };

        ProductPublishRules.Check(visa, Today).Should().BeEmpty("an application can be made on any day");
    }

    [Fact]
    public void A_visa_needs_its_details()
    {
        var problems = ProductPublishRules.Check(Visa() with { Visa = null }, Today);

        Fields(problems).Should().Equal("visa");
    }

    [Fact]
    public void A_visa_needs_at_least_one_document_on_its_checklist()
    {
        var visa = Visa();
        var problems = ProductPublishRules.Check(visa with { Visa = visa.Visa! with { Documents = [] } }, Today);

        Fields(problems).Should().Equal("visa.documents");
    }

    [Fact]
    public void A_visa_with_details_still_to_fill_in_is_told_each_one()
    {
        var visa = Visa();
        var unfinished = visa with
        {
            Visa = visa.Visa! with { VisaType = string.Empty, ProcessingTimeDays = 0, ValidityDays = 0, Documents = [] },
        };

        Fields(ProductPublishRules.Check(unfinished, Today)).Should().BeEquivalentTo(
            ["visa.visaType", "visa.processingTimeDays", "visa.validityDays", "visa.documents"]);
    }

    [Fact]
    public void A_product_with_many_problems_gets_a_message_for_each_written_for_the_agent()
    {
        var problems = ProductPublishRules.Check(Blank(ProductType.Visa), Today);

        Fields(problems).Should().BeEquivalentTo(["title", "basePriceMinor", "media", "visa"]);
        problems.Should().OnlyContain(problem => problem.Message.EndsWith('.') && problem.Message.Length > 10);
    }
}
