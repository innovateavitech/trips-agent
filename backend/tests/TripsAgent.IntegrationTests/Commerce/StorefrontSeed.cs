using FluentAssertions;
using TripsAgent.Domain.Assets;
using TripsAgent.Domain.Catalog;
using TripsAgent.Domain.Common;
using TripsAgent.IntegrationTests.Suppliers;

namespace TripsAgent.IntegrationTests.Commerce;

/// <summary>
/// The catalog a traveller shops from: a published tour, and dated departures on it.
/// </summary>
/// <remarks>
/// Shared by the checkout tests and the public departure tests, because both are about the same
/// agency's shop and a second copy of this would drift from the first.
/// </remarks>
internal static class StorefrontSeed
{
    /// <summary>₦150,000 a head, as the agent typed it on the product.</summary>
    public static readonly Money TourPrice = Money.FromMajor(150_000);

    /// <summary>A published tour at <see cref="TourPrice"/> a head, which a traveller can put in a cart.</summary>
    public static async Task<Guid> TourAsync(BookingPipelineHarness harness, bool published = true)
    {
        var now = harness.Clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        await using var db = harness.AsAgency();

        // A published product needs an image, so the storefront has something to show.
        var image = Asset.Reserve(harness.AgencyId, AssetPurpose.ProductMedia, "kilimanjaro.jpg", now.AddMinutes(15));
        image.RecordUpload("image/jpeg", 250_000);
        image.TryBeginProcessing(now);
        image.RecordCleanScan(now);
        image.MarkReady(1_600, 1_067, now);
        db.Assets.Add(image);
        await db.SaveChangesAsync();

        var slug = published ? "kilimanjaro-seven-days" : "kilimanjaro-draft";

        var product = Product.CreateDraft(
            harness.AgencyId,
            new ProductContent
            {
                ProductType = ProductType.Tour,
                Title = "Kilimanjaro, seven days",
                Summary = "Machame route, seven days, small group.",
                Description = "Seven days on the Machame route, with a night in Moshi either side.",
                Currency = "NGN",
                BasePriceMinor = TourPrice,
                AvailableFrom = today.AddDays(10),
                HeroAssetId = image.Id,
                Media = [new ProductMediaContent(image.Id, "Uhuru Peak at dawn")],
            },
            slug);

        if (published)
        {
            product.TryPublish(now, today, out var problems)
                .Should().BeTrue("the seeded tour has everything a published product needs: {0}", problems);
        }

        db.Products.Add(product);
        await db.SaveChangesAsync();

        return product.Id;
    }

    /// <summary>A dated group departure on that tour: ten seats, <see cref="TourPrice"/> a head.</summary>
    public static async Task<Guid> DepartureAsync(
        BookingPipelineHarness harness,
        Guid productId,
        int depositPercentBasisPoints = 0,
        IReadOnlyList<PriceTierTerms>? priceTiers = null)
    {
        var now = harness.Clock.GetUtcNow();
        await using var db = harness.AsAgency();

        var terms = new DepartureTerms
        {
            DepartureDate = DateOnly.FromDateTime(now.UtcDateTime).AddDays(180),
            IsGroupDeparture = true,
            MinPax = 4,
            CapacityTotal = 10,
            CutoffDaysBefore = 14,
            DepositType = depositPercentBasisPoints > 0 ? DepositType.Percent : DepositType.None,
            DepositPercentBasisPoints = depositPercentBasisPoints > 0 ? depositPercentBasisPoints : null,
            PriceTiers = priceTiers ?? [new PriceTierTerms(1, null, TourPrice)],
        };

        var departure = Departure.Create(harness.AgencyId, productId, terms, now.AddDays(166), now);
        db.Departures.Add(departure);
        await db.SaveChangesAsync();

        return departure.Id;
    }
}
