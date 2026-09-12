using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Catalog;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.Application.Catalog;

/// <summary>A departure, and what the console shows alongside it.</summary>
/// <param name="ProductTitle">The tour or package this is a run of.</param>
/// <param name="Currency">The product's currency — the agency's own, for now (open question 17).</param>
/// <param name="WaitlistCount">How many people are waiting or hold an open offer.</param>
public sealed record DepartureView(
    Departure Departure,
    string ProductTitle,
    string Currency,
    int WaitlistCount);

/// <summary>One traveller on the manifest an agent hands the operator.</summary>
/// <param name="IsConfirmed">True once the booking is paid; false while it is only held.</param>
public sealed record ManifestRow(
    string OrderReference,
    string TravellerName,
    TravellerType PaxType,
    string? Room,
    bool IsConfirmed);

/// <summary>What to list. Both filters are optional.</summary>
/// <param name="From">Only departures leaving on or after this day.</param>
public sealed record DepartureListFilter(Guid? ProductId, DateOnly? From);

/// <summary>What came of a change to a departure.</summary>
public abstract record DepartureChangeOutcome
{
    private DepartureChangeOutcome()
    {
    }

    /// <summary>The change was saved. <paramref name="View"/> is the departure as it now stands.</summary>
    public sealed record Saved(DepartureView View) : DepartureChangeOutcome;

    /// <summary>The departure cannot be stored like this. <paramref name="Problems"/> lists every reason.</summary>
    public sealed record Invalid(IReadOnlyList<ProductProblem> Problems) : DepartureChangeOutcome;

    /// <summary>No departure — or no product — with that id belongs to this agency.</summary>
    public sealed record NotFound : DepartureChangeOutcome;

    /// <summary>The product is a visa, and visas have no departures.</summary>
    public sealed record ProductHasNoDepartures : DepartureChangeOutcome;

    /// <summary>
    /// Well-formed, but the departure is not in a state to take it: closing a closed one, editing a
    /// cancelled one, dropping the capacity below the seats already sold.
    /// </summary>
    public sealed record WrongStatus(string Reason) : DepartureChangeOutcome;

    /// <summary>Someone else saved the departure between this request reading it and writing it.</summary>
    public sealed record ChangedElsewhere : DepartureChangeOutcome;
}

/// <summary>
/// The agency's dated departures: building them, selling them, closing them and calling them off
/// (build plan F6, issue #57).
/// </summary>
/// <remarks>
/// <para>
/// <b>Rules live in the domain.</b> <see cref="DepartureRules"/> decides what can be stored and
/// <see cref="DepartureStatusRules"/> what status the seats give it. This class adds only what needs
/// the database: that the product is the agency's own and can have departures, and the cutoff
/// instant, which needs the agency's time zone.
/// </para>
/// <para>
/// <b>Seats are not moved here.</b> They are moved by <see cref="DepartureSeats"/>, with one atomic
/// <c>UPDATE</c> under the database's own no-oversell CHECK. This class only ever reads them.
/// </para>
/// <para>
/// Another agency's departure looks exactly like a missing one: the tenant filter hides it, and
/// row-level security hides it again below that.
/// </para>
/// </remarks>
public sealed class DepartureService
{
    private readonly IAppDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly TimeProvider _clock;

    public DepartureService(IAppDbContext db, ITenantContext tenant, TimeProvider clock)
    {
        _db = db;
        _tenant = tenant;
        _clock = clock;
    }

    /// <summary>The agency's departures, soonest first.</summary>
    public async Task<IReadOnlyList<DepartureView>> ListAsync(
        DepartureListFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var query = WithEveryPart(_db.Departures.AsNoTracking());

        if (filter.ProductId is { } productId)
        {
            query = query.Where(departure => departure.ProductId == productId);
        }

        if (filter.From is { } from)
        {
            query = query.Where(departure => departure.DepartureDate >= from);
        }

        var departures = await query
            .OrderBy(departure => departure.DepartureDate)
            .ThenBy(departure => departure.Id)
            .ToListAsync(cancellationToken);

        return await DescribeAsync(departures, cancellationToken);
    }

    /// <summary>One departure, or null when the agency has none with that id.</summary>
    public async Task<DepartureView?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var departure = await WithEveryPart(_db.Departures.AsNoTracking())
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        return departure is null ? null : (await DescribeAsync([departure], cancellationToken))[0];
    }

    /// <summary>Adds a departure to one of the agency's tours or packages.</summary>
    public async Task<DepartureChangeOutcome> CreateAsync(
        Guid productId,
        DepartureTerms terms,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(terms);

        var product = await _db.Products.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == productId, cancellationToken);

        if (product is null)
        {
            return new DepartureChangeOutcome.NotFound();
        }

        if (product.ProductType == ProductType.Visa)
        {
            return new DepartureChangeOutcome.ProductHasNoDepartures();
        }

        var (agency, today) = await CurrentAgencyAsync(cancellationToken);
        var problems = DepartureRules.Validate(terms, today);

        if (problems.Count > 0)
        {
            return new DepartureChangeOutcome.Invalid(problems);
        }

        var departure = Departure.Create(
            agency.Id, productId, terms, CutoffAt(terms, agency), _clock.GetUtcNow());

        _db.Departures.Add(departure);

        return await SaveAsync(departure, cancellationToken);
    }

    /// <summary>
    /// Saves the whole departure. The <paramref name="version"/> is the one the console read it at;
    /// a stale one means somebody else saved in between, and this save is refused rather than
    /// silently undoing theirs.
    /// </summary>
    public async Task<DepartureChangeOutcome> SaveAsync(
        Guid id,
        DepartureTerms terms,
        int version,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(terms);

        var departure = await WithEveryPart(_db.Departures).FirstOrDefaultAsync(
            candidate => candidate.Id == id, cancellationToken);

        if (departure is null)
        {
            return new DepartureChangeOutcome.NotFound();
        }

        if (departure.Version != version)
        {
            return new DepartureChangeOutcome.ChangedElsewhere();
        }

        var (agency, today) = await CurrentAgencyAsync(cancellationToken);

        // The date is only checked against today when it moves. Otherwise a departure that has
        // already left could never have its rooms or its waitlist touched again.
        var problems = departure.DepartureDate == terms.DepartureDate
            ? DepartureRules.Validate(terms, DateOnly.MinValue)
            : DepartureRules.Validate(terms, today);

        if (problems.Count > 0)
        {
            return new DepartureChangeOutcome.Invalid(problems);
        }

        try
        {
            departure.Revise(terms, CutoffAt(terms, agency), _clock.GetUtcNow());
        }
        catch (InvalidOperationException ex)
        {
            return new DepartureChangeOutcome.WrongStatus(ex.Message);
        }

        return await SaveAsync(departure, cancellationToken);
    }

    /// <summary>Stops new bookings. The ones already made stand.</summary>
    public Task<DepartureChangeOutcome> CloseAsync(Guid id, CancellationToken cancellationToken = default) =>
        ChangeAsync(id, (departure, now) => departure.Close(now), cancellationToken);

    /// <summary>Puts it back on sale, at whatever status its seats now give it.</summary>
    public Task<DepartureChangeOutcome> ReopenAsync(Guid id, CancellationToken cancellationToken = default) =>
        ChangeAsync(id, (departure, now) => departure.Reopen(now), cancellationToken);

    /// <summary>
    /// Calls the departure off, and puts every paid booking on it into the resolution queue as a
    /// full refund — build plan decision 12.
    /// </summary>
    /// <remarks>
    /// The money is not moved here. Marking the line <c>FailedNeedsResolution</c> with an open
    /// resolution is what the resolution queue reads, and refunding is its job; doing it in the
    /// same transaction as the cancellation would mean a gateway call inside a request handler.
    /// Seats still held in somebody's checkout are released, because there is nothing left to buy.
    /// </remarks>
    public async Task<DepartureChangeOutcome> CancelAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var departure = await WithEveryPart(_db.Departures).FirstOrDefaultAsync(
            candidate => candidate.Id == id, cancellationToken);

        if (departure is null)
        {
            return new DepartureChangeOutcome.NotFound();
        }

        var now = _clock.GetUtcNow();

        try
        {
            departure.Cancel(now);
        }
        catch (InvalidOperationException ex)
        {
            return new DepartureChangeOutcome.WrongStatus(ex.Message);
        }

        await ReleaseEveryHoldAsync(departure, now, cancellationToken);
        await RefundEveryBookingAsync(departure, now, cancellationToken);

        return await SaveAsync(departure, cancellationToken);
    }

    /// <summary>
    /// Who is on the departure. Null when the agency has no departure with that id.
    /// </summary>
    public async Task<IReadOnlyList<ManifestRow>?> ManifestAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        if (!await _db.Departures.AnyAsync(departure => departure.Id == id, cancellationToken))
        {
            return null;
        }

        // One query rather than a walk over the manifest rows: a full coach is 50-odd travellers,
        // and 50 round trips to answer one screen is how a list page becomes slow.
        var rows = await (
            from entry in _db.PaxManifests.AsNoTracking()
            join traveller in _db.OrderTravellers.AsNoTracking() on entry.OrderTravellerId equals traveller.Id
            join line in _db.OrderLines.AsNoTracking() on entry.OrderLineId equals line.Id
            join order in _db.Orders.AsNoTracking() on line.OrderId equals order.Id
            where entry.DepartureId == id
            orderby order.OrderNumber, traveller.LastName, traveller.FirstName
            select new
            {
                order.OrderNumber,
                traveller.FirstName,
                traveller.LastName,
                traveller.TravellerType,
                entry.RoomAssignment,
                line.FulfilmentStatus,
            }).ToListAsync(cancellationToken);

        return
        [
            .. rows.Select(row => new ManifestRow(
                row.OrderNumber,
                $"{row.FirstName} {row.LastName}".Trim(),
                row.TravellerType,
                row.RoomAssignment,
                row.FulfilmentStatus == FulfilmentStatus.Confirmed)),
        ];
    }

    /// <summary>Who is waiting for a seat, in the order they joined. Null when there is no such departure.</summary>
    public async Task<IReadOnlyList<DepartureWaitlistEntry>?> WaitlistAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        if (!await _db.Departures.AnyAsync(departure => departure.Id == id, cancellationToken))
        {
            return null;
        }

        return await _db.DepartureWaitlist.AsNoTracking()
            .Where(entry => entry.DepartureId == id)
            .OrderBy(entry => entry.JoinedAt)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Puts somebody on the waitlist, or brings them back to the front of it if they are already on
    /// it and their interest lapsed. Null when there is no such departure.
    /// </summary>
    public async Task<DepartureWaitlistEntry?> JoinWaitlistAsync(
        Guid departureId,
        string name,
        string email,
        int paxCount,
        CancellationToken cancellationToken = default)
    {
        var departure = await _db.Departures.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == departureId, cancellationToken);

        if (departure is null)
        {
            return null;
        }

        var now = _clock.GetUtcNow();

        // citext makes this match however they capitalised their address, which is the point: the
        // unique index would otherwise refuse the second join with a 500 nobody can act on.
        var existing = await _db.DepartureWaitlist.FirstOrDefaultAsync(
            entry => entry.DepartureId == departureId && entry.Email == email.Trim(), cancellationToken);

        if (existing is not null)
        {
            return existing;
        }

        var joined = DepartureWaitlistEntry.Join(
            departure.AgencyId, departureId, name, email, paxCount, now);

        _db.DepartureWaitlist.Add(joined);
        await _db.SaveChangesAsync(cancellationToken);

        return joined;
    }

    /// <summary>The instant bookings close: midnight, that many days before, where the agency is.</summary>
    /// <remarks>
    /// The agency's own midnight rather than UTC's, for the reason a booking window uses the
    /// agency's today: a cutoff 14 days out means 14 days out in Lagos, not an hour earlier.
    /// </remarks>
    internal static DateTimeOffset CutoffAt(DepartureTerms terms, Agency agency)
    {
        ArgumentNullException.ThrowIfNull(terms);
        ArgumentNullException.ThrowIfNull(agency);

        var day = terms.DepartureDate.AddDays(-terms.CutoffDaysBefore);
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);

        if (!TimeZoneInfo.TryFindSystemTimeZoneById(agency.Timezone, out var zone))
        {
            return new DateTimeOffset(local, TimeSpan.Zero);
        }

        // Midnight does not exist on the day a zone springs forward. The first instant that does is
        // the right answer, and it is one offset step later. Lagos never does this; others do.
        if (zone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    private async Task<DepartureChangeOutcome> ChangeAsync(
        Guid id,
        Action<Departure, DateTimeOffset> change,
        CancellationToken cancellationToken)
    {
        var departure = await WithEveryPart(_db.Departures).FirstOrDefaultAsync(
            candidate => candidate.Id == id, cancellationToken);

        if (departure is null)
        {
            return new DepartureChangeOutcome.NotFound();
        }

        try
        {
            change(departure, _clock.GetUtcNow());
        }
        catch (InvalidOperationException ex)
        {
            return new DepartureChangeOutcome.WrongStatus(ex.Message);
        }

        return await SaveAsync(departure, cancellationToken);
    }

    private async Task<DepartureChangeOutcome> SaveAsync(Departure departure, CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Two saves of the same version: the database refused the second. The rejected rows stay
            // tracked until cleared, and would fail the next save too.
            _db.ChangeTracker.Clear();
            return new DepartureChangeOutcome.ChangedElsewhere();
        }

        return new DepartureChangeOutcome.Saved(
            (await DescribeAsync([departure], cancellationToken))[0]);
    }

    /// <summary>Releases every seat still held in somebody's checkout, and gives the capacity back.</summary>
    private async Task ReleaseEveryHoldAsync(
        Departure departure,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var holds = await _db.DepartureHolds
            .Where(hold => hold.DepartureId == departure.Id && hold.Status == DepartureHoldStatus.Held)
            .ToListAsync(cancellationToken);

        if (holds.Count == 0)
        {
            return;
        }

        var released = 0;

        foreach (var hold in holds)
        {
            if (hold.Release(now))
            {
                released += hold.PaxCount;
            }
        }

        await _db.Departures
            .Where(candidate => candidate.Id == departure.Id)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(
                        candidate => candidate.CapacityReserved,
                        candidate => candidate.CapacityReserved - released)
                    .SetProperty(candidate => candidate.UpdatedAt, now),
                cancellationToken);
    }

    /// <summary>
    /// Puts every booking on the departure that has not already failed or been refunded into the
    /// resolution queue, with the reason the agent will read there (decision 12).
    /// </summary>
    private async Task RefundEveryBookingAsync(
        Departure departure,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var lineIds = await _db.PaxManifests.AsNoTracking()
            .Where(entry => entry.DepartureId == departure.Id)
            .Select(entry => entry.OrderLineId)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (lineIds.Count == 0)
        {
            return;
        }

        var lines = await _db.OrderLines
            .Where(line => lineIds.Contains(line.Id))
            .Where(line => line.FulfilmentStatus == FulfilmentStatus.Reserved
                        || line.FulfilmentStatus == FulfilmentStatus.Confirming
                        || line.FulfilmentStatus == FulfilmentStatus.Confirmed)
            .ToListAsync(cancellationToken);

        foreach (var line in lines)
        {
            line.RecordFulfilment(
                FulfilmentStatus.FailedNeedsResolution,
                now,
                "The agency cancelled this departure. Refund the traveller in full.");
        }
    }

    /// <summary>Fills in the product title, the currency and the waitlist count, in two queries.</summary>
    private async Task<IReadOnlyList<DepartureView>> DescribeAsync(
        List<Departure> departures,
        CancellationToken cancellationToken)
    {
        if (departures.Count == 0)
        {
            return [];
        }

        var productIds = departures.Select(departure => departure.ProductId).Distinct().ToList();
        var departureIds = departures.Select(departure => departure.Id).Distinct().ToList();

        var products = await _db.Products.AsNoTracking()
            .Where(product => productIds.Contains(product.Id))
            .Select(product => new { product.Id, product.Title, product.Currency })
            .ToDictionaryAsync(product => product.Id, cancellationToken);

        var waiting = await _db.DepartureWaitlist.AsNoTracking()
            .Where(entry => departureIds.Contains(entry.DepartureId))
            .Where(entry => entry.Status == WaitlistStatus.Waiting || entry.Status == WaitlistStatus.Offered)
            .GroupBy(entry => entry.DepartureId)
            .Select(group => new { DepartureId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(group => group.DepartureId, group => group.Count, cancellationToken);

        return
        [
            .. departures.Select(departure =>
            {
                var product = products.GetValueOrDefault(departure.ProductId);

                return new DepartureView(
                    departure,
                    product?.Title ?? string.Empty,
                    product?.Currency ?? string.Empty,
                    waiting.GetValueOrDefault(departure.Id));
            }),
        ];
    }

    /// <summary>The calling agency, and today's date where it is.</summary>
    private async Task<(Agency Agency, DateOnly Today)> CurrentAgencyAsync(CancellationToken cancellationToken)
    {
        var agencyId = _tenant.AgencyId ?? throw new InvalidOperationException(
            "Departures belong to an agency, and none is resolved for this request.");

        var agency = await _db.Agencies.AsNoTracking()
            .FirstAsync(candidate => candidate.Id == agencyId, cancellationToken);

        var now = _clock.GetUtcNow();
        var local = TimeZoneInfo.TryFindSystemTimeZoneById(agency.Timezone, out var zone)
            ? TimeZoneInfo.ConvertTime(now, zone)
            : now;

        return (agency, DateOnly.FromDateTime(local.DateTime));
    }

    /// <summary>A departure with the rows it is made of.</summary>
    private static IQueryable<Departure> WithEveryPart(IQueryable<Departure> departures) =>
        departures
            .Include(departure => departure.PriceTiers)
            .Include(departure => departure.Installments!)
            .ThenInclude(plan => plan.Items)
            .AsSplitQuery();
}
