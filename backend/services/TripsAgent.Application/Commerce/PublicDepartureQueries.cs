using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Pricing;
using TripsAgent.Contracts.Commerce;
using TripsAgent.Domain.Catalog;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Pricing;

namespace TripsAgent.Application.Commerce;

/// <summary>
/// The dated departures a traveller can buy seats on, as the agency's own site shows them
/// (build plan F4 and F6).
/// </summary>
/// <remarks>
/// <para>
/// <b>Priced for the party being asked about.</b> A departure's price bands are per head and depend
/// on how many people are travelling, so a page that shows one number has to be told the party size.
/// The figures here are sell prices, through the same <see cref="PricingService"/> the cart uses —
/// never the agent's own cost, which is not a traveller's business (CLAUDE.md rule 4).
/// </para>
/// <para>
/// <b>What is on the page is what checkout will do.</b> The payment plan shown is
/// <see cref="DeparturePaymentPlan"/>'s, worked out from the same terms that will bill the booking,
/// so a traveller who reads "₦75,000 today" pays ₦75,000 today. It is a preview and nothing is
/// stored: the schedule that binds is written when they buy.
/// </para>
/// <para>
/// Read inside the agency the host name resolved to, and only for a published product — a draft's
/// departures are as invisible as the draft is.
/// </para>
/// </remarks>
public sealed class PublicDepartureQueries
{
    private readonly IAppDbContext _db;
    private readonly StorefrontTenant _storefront;
    private readonly PricingService _pricing;
    private readonly TimeProvider _clock;

    public PublicDepartureQueries(
        IAppDbContext db,
        StorefrontTenant storefront,
        PricingService pricing,
        TimeProvider clock)
    {
        _db = db;
        _storefront = storefront;
        _pricing = pricing;
        _clock = clock;
    }

    /// <summary>Every departure still on sale for one published product, soonest first.</summary>
    /// <param name="host">The host name the traveller's browser used.</param>
    /// <param name="productSlug">The product's slug, as its page URL carries it.</param>
    /// <param name="party">Who is travelling, which decides the price band.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public async Task<StoreResult<IReadOnlyList<PublicDepartureResponse>>> ForProductAsync(
        string? host,
        string productSlug,
        PartySize party,
        CancellationToken cancellationToken = default)
    {
        if (await _storefront.EnterAsync(host, cancellationToken) is null)
        {
            return Store.NotFound<IReadOnlyList<PublicDepartureResponse>>(NoSuchTrip);
        }

        var product = await PublishedProductAsync(candidate => candidate.Slug == productSlug, cancellationToken);

        if (product is null)
        {
            return Store.NotFound<IReadOnlyList<PublicDepartureResponse>>(NoSuchTrip);
        }

        var now = _clock.GetUtcNow();

        var departures = await _db.Departures.AsNoTracking()
            .Include(candidate => candidate.PriceTiers)
            .Include(candidate => candidate.Installments!)
            .ThenInclude(plan => plan.Items)
            .Where(candidate => candidate.ProductId == product.Id)
            .Where(candidate => candidate.Status != DepartureStatus.Cancelled && candidate.CutoffAt > now)
            .OrderBy(candidate => candidate.DepartureDate)
            .ToListAsync(cancellationToken);

        var shown = new List<PublicDepartureResponse>(departures.Count);

        foreach (var departure in departures)
        {
            if (await DescribeAsync(departure, product, party, cancellationToken) is { } described)
            {
                shown.Add(described);
            }
        }

        return new StoreResult<IReadOnlyList<PublicDepartureResponse>>.Done(shown);
    }

    /// <summary>One departure, priced for this party.</summary>
    public async Task<StoreResult<PublicDepartureResponse>> FindAsync(
        string? host,
        Guid departureId,
        PartySize party,
        CancellationToken cancellationToken = default)
    {
        if (await _storefront.EnterAsync(host, cancellationToken) is null)
        {
            return Store.NotFound<PublicDepartureResponse>(NoSuchTrip);
        }

        var departure = await _db.Departures.AsNoTracking()
            .Include(candidate => candidate.PriceTiers)
            .Include(candidate => candidate.Installments!)
            .ThenInclude(plan => plan.Items)
            .FirstOrDefaultAsync(candidate => candidate.Id == departureId, cancellationToken);

        if (departure is null || departure.Status == DepartureStatus.Cancelled)
        {
            return Store.NotFound<PublicDepartureResponse>(NoSuchTrip);
        }

        var product = await PublishedProductAsync(candidate => candidate.Id == departure.ProductId, cancellationToken);

        if (product is null)
        {
            return Store.NotFound<PublicDepartureResponse>(NoSuchTrip);
        }

        var described = await DescribeAsync(departure, product, party, cancellationToken);

        return described is null
            ? Store.NotFound<PublicDepartureResponse>(NoSuchTrip)
            : new StoreResult<PublicDepartureResponse>.Done(described);
    }

    /// <summary>One departure as a traveller sees it. Null when no band covers a party this size.</summary>
    private async Task<PublicDepartureResponse?> DescribeAsync(
        Departure departure,
        PublishedProduct product,
        PartySize party,
        CancellationToken cancellationToken)
    {
        var terms = departure.ToTerms();
        var pax = Math.Max(party.Total, 1);
        var perPax = DepartureRules.PriceForParty(terms.PriceTiers, pax);

        if (perPax is not { } netPerPax)
        {
            return null;
        }

        // The agency's markup on top of the seat price it typed, the same way the cart prices it.
        var subject = new PricingSubject(PricedProductType.GroupDeparture, product.Currency, product.Id);
        var sell = await _pricing.PriceAsync(subject, netPerPax * pax, cancellationToken);

        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);

        // Only a departure the agent actually sells on a plan has one. One with no deposit and no
        // instalments has a schedule in the domain — a single line due at the cutoff — but that is
        // when the agency would chase an agent's customer for it, not when a card is charged: a
        // traveller buying it here pays for it there and then, like any other product. So there is
        // no plan to show and nothing is put off, which is exactly what checkout does.
        var soldOnAPlan = terms.DepositType != DepositType.None || terms.Installments.Count > 0;

        var payments = soldOnAPlan
            ? DeparturePaymentPlan.Build(terms, today, pax, netPerPax)
            : [];

        // What is really payable today: everything the plan does not put off, plus the whole of the
        // agency's margin, which checkout collects with the deposit. This has to match
        // StorefrontCheckoutService.AmountDueNowAsync to the kobo — a page promising "nothing today"
        // over a checkout that charges in full is the worst thing this page could say.
        var deferred = payments.Where(payment => !payment.DueOnBooking).Sum(payment => payment.AmountMinor.AmountMinor);
        var dueNow = sell.GrossAmountMinor.AmountMinor - deferred;

        return new PublicDepartureResponse(
            departure.Id,
            product.Slug,
            product.Title,
            departure.DepartureDate,
            departure.Status.ToString(),
            departure.IsGroupDeparture,
            departure.MinPax,
            departure.Seats.SeatsLeft,
            product.Currency,
            sell.GrossAmountMinor.AmountMinor / pax,
            sell.GrossAmountMinor.AmountMinor,
            dueNow,
            departure.CutoffAt,
            [.. terms.PriceTiers.Select(tier =>
                new DeparturePriceBandResponse(tier.MinPax, tier.MaxPax, tier.PricePerPaxMinor.AmountMinor))],
            [.. payments.Select(payment =>
                new DeparturePaymentResponse(
                    payment.Sequence, payment.Label, payment.DueDate, payment.AmountMinor.AmountMinor))]);
    }

    private Task<PublishedProduct?> PublishedProductAsync(
        System.Linq.Expressions.Expression<Func<Domain.Catalog.Product, bool>> match,
        CancellationToken cancellationToken) =>
        _db.Products.AsNoTracking()
            .Where(match)
            .Where(candidate => candidate.Status == ProductStatus.Published)
            .Select(candidate => new PublishedProduct(candidate.Id, candidate.Slug, candidate.Title, candidate.Currency))
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>The little a departure page needs to know about the product it belongs to.</summary>
    private sealed record PublishedProduct(Guid Id, string Slug, string Title, string Currency);

    private const string NoSuchTrip = "We could not find that trip.";
}
