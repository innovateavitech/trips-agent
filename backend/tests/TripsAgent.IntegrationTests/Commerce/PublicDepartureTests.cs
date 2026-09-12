using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TripsAgent.Application.Catalog;
using TripsAgent.Application.Commerce;
using TripsAgent.Contracts.Commerce;
using TripsAgent.Domain.Catalog;
using TripsAgent.Domain.Common;
using TripsAgent.IntegrationTests.Persistence;
using TripsAgent.IntegrationTests.Suppliers;

namespace TripsAgent.IntegrationTests.Commerce;

/// <summary>
/// The dated departures a traveller sees on the agency's own site, against real PostgreSQL
/// (build plan F5 and F6).
/// </summary>
/// <remarks>
/// <para>
/// What these tests are really about is the promise the page makes. A traveller reading "₦75,000
/// today" has to be charged ₦75,000 today, and "4 seats left" has to mean four seats nobody else is
/// already holding. Both come out of the same rules the checkout uses, which is why they are checked
/// here rather than against a stub.
/// </para>
/// <para>
/// Everything is read anonymously, through the host name the browser used, inside the agency that
/// resolved to. There is no <c>IgnoreQueryFilters</c> in this file and there must not be.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class PublicDepartureTests : IAsyncLifetime
{
    private const string Host = FixedStorefrontDirectory.Host;

    private static readonly Money TourPrice = StorefrontSeed.TourPrice;

    private readonly PostgresFixture _postgres;
    private TripsAfricaStub _stub = null!;

    public PublicDepartureTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync() => _stub = await TripsAfricaStub.StartAsync();

    public async Task DisposeAsync() => await _stub.DisposeAsync();

    [Fact]
    public async Task The_dates_on_sale_are_priced_for_the_party_that_asked()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var productId = await StorefrontSeed.TourAsync(harness);
        var departureId = await StorefrontSeed.DepartureAsync(harness, productId);

        var shown = await ForProductAsync(harness, "kilimanjaro-seven-days", new PartySize(2, 0, 0));

        var departure = shown.Should().ContainSingle().Which;
        departure.Id.Should().Be(departureId);
        departure.ProductTitle.Should().Be("Kilimanjaro, seven days");
        departure.SeatsLeft.Should().Be(10);
        departure.MinPax.Should().Be(4);

        // No markup rule, so the traveller pays what the agent typed — twice, for two of them.
        departure.PricePerPaxMinor.Should().Be(TourPrice.AmountMinor);
        departure.TotalMinor.Should().Be(TourPrice.AmountMinor * 2);

        // This one is not sold on a plan, so there is no plan to show and the whole price is due at
        // checkout — the same figure StorefrontCheckoutService will ask the gateway for.
        departure.DueNowMinor.Should().Be(departure.TotalMinor);
        departure.Payments.Should().BeEmpty();
    }

    [Fact]
    public async Task A_deposit_says_what_is_due_today_and_when_the_rest_falls_due()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var productId = await StorefrontSeed.TourAsync(harness);
        await StorefrontSeed.DepartureAsync(harness, productId, depositPercentBasisPoints: 2_500);

        var shown = await ForProductAsync(harness, "kilimanjaro-seven-days", new PartySize(2, 0, 0));
        var departure = shown.Should().ContainSingle().Which;

        // A quarter of ₦300,000 today, the rest by the cutoff.
        departure.DueNowMinor.Should().Be(Money.FromMajor(75_000).AmountMinor);
        departure.Payments.Should().HaveCount(2);
        departure.Payments[0].Label.Should().Be("Deposit");
        departure.Payments[0].AmountMinor.Should().Be(Money.FromMajor(75_000).AmountMinor);
        departure.Payments[1].Label.Should().Be("Balance");
        departure.Payments[1].AmountMinor.Should().Be(Money.FromMajor(225_000).AmountMinor);

        // The schedule adds up to the price. A plan that loses a kobo is a plan nobody can reconcile.
        departure.Payments.Sum(payment => payment.AmountMinor).Should().Be(departure.TotalMinor);
        departure.Payments[1].DueDate.Should().Be(departure.DepartureDate.AddDays(-14));
    }

    [Fact]
    public async Task A_bigger_party_falls_into_the_band_that_covers_it()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var productId = await StorefrontSeed.TourAsync(harness);
        await StorefrontSeed.DepartureAsync(
            harness,
            productId,
            priceTiers:
            [
                new PriceTierTerms(1, 3, TourPrice),
                new PriceTierTerms(4, null, Money.FromMajor(120_000)),
            ]);

        var alone = await ForProductAsync(harness, "kilimanjaro-seven-days", new PartySize(1, 0, 0));
        var together = await ForProductAsync(harness, "kilimanjaro-seven-days", new PartySize(2, 2, 0));

        alone.Single().PricePerPaxMinor.Should().Be(TourPrice.AmountMinor);
        together.Single().PricePerPaxMinor.Should().Be(Money.FromMajor(120_000).AmountMinor);
        together.Single().TotalMinor.Should().Be(Money.FromMajor(480_000).AmountMinor);

        // Both bands are on the page, so a party of three can see what a fourth would save them.
        alone.Single().PriceBands.Should().HaveCount(2);
    }

    [Fact]
    public async Task Seats_somebody_else_is_holding_are_gone_from_the_count()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var productId = await StorefrontSeed.TourAsync(harness);
        var departureId = await StorefrontSeed.DepartureAsync(harness, productId);

        var before = await ForProductAsync(harness, "kilimanjaro-seven-days", new PartySize(1, 0, 0));
        before.Single().SeatsLeft.Should().Be(10);

        // Another traveller reaches the payment page with two of the ten seats.
        var cart = await harness.InAgencyScopeAsync(async provider =>
        {
            var outcome = await provider.GetRequiredService<CartService>()
                .AddAsync(Host, null, new AddCartItemRequest(DepartureId: departureId, Adults: 2));

            return outcome.Should().BeOfType<StoreResult<CartResponse>.Done>().Which.Value;
        });

        var held = await harness.InAgencyScopeAsync(provider =>
            provider.GetRequiredService<DepartureSeats>().HoldAsync(departureId, cart.Id, 2));

        held.Should().BeOfType<SeatHoldOutcome.Held>();

        var afterHold = await ForProductAsync(harness, "kilimanjaro-seven-days", new PartySize(1, 0, 0));

        afterHold.Single().SeatsLeft.Should().Be(8, "a seat a checkout is holding is a seat nobody else can buy");
    }

    [Fact]
    public async Task A_trip_that_is_not_published_has_no_dates_to_show()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var draftId = await StorefrontSeed.TourAsync(harness, published: false);
        var departureId = await StorefrontSeed.DepartureAsync(harness, draftId);

        var byProduct = await harness.InAgencyScopeAsync(provider =>
            provider.GetRequiredService<PublicDepartureQueries>()
                .ForProductAsync(Host, "kilimanjaro-draft", new PartySize(1, 0, 0)));

        var direct = await harness.InAgencyScopeAsync(provider =>
            provider.GetRequiredService<PublicDepartureQueries>()
                .FindAsync(Host, departureId, new PartySize(1, 0, 0)));

        // A draft's departures are as invisible as the draft is, by either route.
        byProduct.Should().BeOfType<StoreResult<IReadOnlyList<PublicDepartureResponse>>.NotFound>();
        direct.Should().BeOfType<StoreResult<PublicDepartureResponse>.NotFound>();
    }

    [Fact]
    public async Task One_departure_reads_the_same_on_its_own_page()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var productId = await StorefrontSeed.TourAsync(harness);
        var departureId = await StorefrontSeed.DepartureAsync(harness, productId, depositPercentBasisPoints: 2_500);

        var outcome = await harness.InAgencyScopeAsync(provider =>
            provider.GetRequiredService<PublicDepartureQueries>()
                .FindAsync(Host, departureId, new PartySize(2, 0, 0)));

        var departure = outcome.Should().BeOfType<StoreResult<PublicDepartureResponse>.Done>().Which.Value;
        departure.ProductSlug.Should().Be("kilimanjaro-seven-days");
        departure.DueNowMinor.Should().Be(Money.FromMajor(75_000).AmountMinor);
        departure.IsGroupDeparture.Should().BeTrue();
    }

    [Fact]
    public async Task A_cancelled_date_is_off_the_site_altogether()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var productId = await StorefrontSeed.TourAsync(harness);
        var departureId = await StorefrontSeed.DepartureAsync(harness, productId);

        await using (var db = harness.AsAgency())
        {
            var departure = await db.Departures.SingleAsync(candidate => candidate.Id == departureId);
            departure.Cancel(harness.Clock.GetUtcNow());
            await db.SaveChangesAsync();
        }

        var shown = await ForProductAsync(harness, "kilimanjaro-seven-days", new PartySize(1, 0, 0));

        var direct = await harness.InAgencyScopeAsync(provider =>
            provider.GetRequiredService<PublicDepartureQueries>()
                .FindAsync(Host, departureId, new PartySize(1, 0, 0)));

        shown.Should().BeEmpty();
        direct.Should().BeOfType<StoreResult<PublicDepartureResponse>.NotFound>();
    }

    [Fact]
    public async Task Another_shops_host_finds_none_of_these_dates()
    {
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, _stub);
        var productId = await StorefrontSeed.TourAsync(harness);
        var departureId = await StorefrontSeed.DepartureAsync(harness, productId);

        var byProduct = await harness.InAgencyScopeAsync(provider =>
            provider.GetRequiredService<PublicDepartureQueries>()
                .ForProductAsync("someone-elses-shop.test", "kilimanjaro-seven-days", new PartySize(1, 0, 0)));

        var direct = await harness.InAgencyScopeAsync(provider =>
            provider.GetRequiredService<PublicDepartureQueries>()
                .FindAsync("someone-elses-shop.test", departureId, new PartySize(1, 0, 0)));

        // The same 404 an unknown trip gets: nobody anonymous learns which agencies are on the platform.
        byProduct.Should().BeOfType<StoreResult<IReadOnlyList<PublicDepartureResponse>>.NotFound>();
        direct.Should().BeOfType<StoreResult<PublicDepartureResponse>.NotFound>();
    }

    private static Task<IReadOnlyList<PublicDepartureResponse>> ForProductAsync(
        BookingPipelineHarness harness,
        string slug,
        PartySize party) =>
        harness.InAgencyScopeAsync(async provider =>
        {
            var outcome = await provider.GetRequiredService<PublicDepartureQueries>()
                .ForProductAsync(Host, slug, party);

            return outcome.Should()
                .BeOfType<StoreResult<IReadOnlyList<PublicDepartureResponse>>.Done>().Which.Value;
        });
}
