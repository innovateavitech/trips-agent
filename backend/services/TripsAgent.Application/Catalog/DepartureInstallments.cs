using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Domain.Catalog;
using TripsAgent.Domain.Common;

namespace TripsAgent.Application.Catalog;

/// <summary>What came of asking for a booking's payment schedule.</summary>
public abstract record InstallmentScheduleOutcome
{
    private InstallmentScheduleOutcome()
    {
    }

    /// <summary>The bill. The same one on a second call: a schedule is written once per line.</summary>
    public sealed record Scheduled(BookingPaymentSchedule Schedule) : InstallmentScheduleOutcome;

    /// <summary>No departure, or no order line, with that id belongs to this agency.</summary>
    public sealed record NotFound : InstallmentScheduleOutcome;

    /// <summary>No price tier covers a party this size, so there is nothing to bill.</summary>
    public sealed record NoPriceForParty(int PaxCount) : InstallmentScheduleOutcome;
}

/// <summary>
/// Turns a departure's terms into the bill for one booking (build plan F6, plan §3 job 11).
/// </summary>
/// <remarks>
/// <para>
/// The arithmetic is <see cref="DeparturePaymentPlan"/>'s and stays in the domain, where it is
/// testable without a database. This adds the two things that need one: the party's tier price, and
/// writing the result down so a later edit to the departure cannot move it.
/// </para>
/// <para>
/// <b>Written once per order line.</b> Asking again returns what is already there rather than a
/// second bill — a checkout that retries must not double what a traveller owes.
/// </para>
/// </remarks>
public sealed class DepartureInstallments
{
    private readonly IAppDbContext _db;
    private readonly TimeProvider _clock;

    public DepartureInstallments(IAppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>
    /// The bill for a booking of <paramref name="paxCount"/> seats made on
    /// <paramref name="bookedOn"/>, from the departure's terms as they stand now.
    /// </summary>
    public async Task<InstallmentScheduleOutcome> ScheduleAsync(
        Guid departureId,
        Guid orderLineId,
        int paxCount,
        string contactName,
        string? contactEmail,
        DateOnly bookedOn,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(paxCount, 1);

        var existing = await ForLineAsync(orderLineId, cancellationToken);

        if (existing is not null)
        {
            return new InstallmentScheduleOutcome.Scheduled(existing);
        }

        var departure = await _db.Departures.AsNoTracking()
            .Include(candidate => candidate.PriceTiers)
            .Include(candidate => candidate.Installments!)
            .ThenInclude(plan => plan.Items)
            .FirstOrDefaultAsync(candidate => candidate.Id == departureId, cancellationToken);

        if (departure is null)
        {
            return new InstallmentScheduleOutcome.NotFound();
        }

        var line = await _db.OrderLines.AsNoTracking()
            .Where(candidate => candidate.Id == orderLineId)
            .Select(candidate => new { candidate.Currency })
            .FirstOrDefaultAsync(cancellationToken);

        if (line is null)
        {
            return new InstallmentScheduleOutcome.NotFound();
        }

        var terms = departure.ToTerms();
        var price = DepartureRules.PriceForParty(terms.PriceTiers, paxCount);

        if (price is not { } pricePerPax)
        {
            return new InstallmentScheduleOutcome.NoPriceForParty(paxCount);
        }

        var schedule = BookingPaymentSchedule.Create(
            departure.AgencyId,
            departureId,
            orderLineId,
            paxCount,
            line.Currency,
            pricePerPax,
            contactName,
            contactEmail,
            bookedOn,
            DeparturePaymentPlan.Build(terms, bookedOn, paxCount, pricePerPax),
            _clock.GetUtcNow());

        _db.BookingPaymentSchedules.Add(schedule);
        await _db.SaveChangesAsync(cancellationToken);

        return new InstallmentScheduleOutcome.Scheduled(schedule);
    }

    /// <summary>The bill for one order line, or null when it has none.</summary>
    public Task<BookingPaymentSchedule?> ForLineAsync(Guid orderLineId, CancellationToken cancellationToken = default) =>
        _db.BookingPaymentSchedules
            .Include(schedule => schedule.Items)
            .FirstOrDefaultAsync(schedule => schedule.OrderLineId == orderLineId, cancellationToken);

    /// <summary>Settles one payment. Idempotent.</summary>
    /// <returns>True when this call is the one that settled it.</returns>
    public async Task<bool> MarkPaidAsync(Guid installmentId, CancellationToken cancellationToken = default)
    {
        var item = await _db.BookingInstallments.FirstOrDefaultAsync(
            candidate => candidate.Id == installmentId, cancellationToken);

        if (item is null || !item.MarkPaid(_clock.GetUtcNow()))
        {
            return false;
        }

        await _db.SaveChangesAsync(cancellationToken);

        return true;
    }

    /// <summary>
    /// Closes what is still owed on a booking — it was refunded, or its departure was called off.
    /// </summary>
    /// <returns>How many payments were closed.</returns>
    public async Task<int> CancelForLineAsync(Guid orderLineId, CancellationToken cancellationToken = default)
    {
        var schedule = await ForLineAsync(orderLineId, cancellationToken);

        if (schedule is null)
        {
            return 0;
        }

        var owed = schedule.Items.Count(item => item.State == InstallmentState.Pending);

        if (owed == 0)
        {
            return 0;
        }

        schedule.Cancel(_clock.GetUtcNow());
        await _db.SaveChangesAsync(cancellationToken);

        return owed;
    }

    /// <summary>What a party of this size pays each, or null when no tier covers them.</summary>
    public static Money? PriceForParty(Departure departure, int paxCount)
    {
        ArgumentNullException.ThrowIfNull(departure);

        return DepartureRules.PriceForParty(
            [.. departure.PriceTiers.Select(tier => new PriceTierTerms(tier.MinPax, tier.MaxPax, tier.PricePerPaxMinor))],
            paxCount);
    }
}
