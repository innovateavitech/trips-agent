using TripsAgent.Domain.Auditing;
using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Catalog;

/// <summary>
/// A dated run of a tour or package, sold by the seat: the date, the capacity, the tiered prices,
/// the deposit and the installment plan (build plan F6, issue #57).
/// </summary>
/// <remarks>
/// <para>
/// <b>The database owns the seats, not this class.</b> <see cref="CapacityReserved"/> and
/// <see cref="CapacityConfirmed"/> are moved by a single atomic <c>UPDATE</c> in the application,
/// under a CHECK constraint that makes <c>reserved + confirmed &gt; total</c> impossible. Two
/// checkouts racing for the last seat cannot both win, however the application is written or
/// however many instances of it are running.
/// </para>
/// <para>
/// <b>The status is worked out, not set.</b> Open → Guaranteed → NearlyFull → SoldOut come from
/// <see cref="DepartureStatusRules"/> and are rewritten whenever the seats move (job 9). Only
/// <see cref="Close"/> and <see cref="Cancel"/> put a status here by hand, and while one of those
/// stands the seats never overwrite it.
/// </para>
/// <para>
/// <b>Version guards the agent's edits, not the seats.</b> A save carries the version it was read
/// at; selling a seat deliberately does not bump it, or every sale would invalidate an editor
/// somebody had open.
/// </para>
/// </remarks>
public sealed class Departure : Entity, IAuditableEntity, ITenantScoped, IAuditLogged
{
    private readonly List<DeparturePriceTier> _priceTiers = [];

    private Departure()
    {
    }

    /// <summary>
    /// A new departure on a product. Throws when <paramref name="terms"/> fails
    /// <see cref="DepartureRules.Validate"/>: the application checks first and turns the problems
    /// into messages, and this is the guard behind that.
    /// </summary>
    /// <param name="cutoffAt">
    /// The instant bookings close, worked out from <see cref="DepartureTerms.CutoffDaysBefore"/> in
    /// the agency's own time zone. Passed in rather than computed here, because the domain has no
    /// business knowing what time zone an agency keeps.
    /// </param>
    public static Departure Create(
        Guid agencyId,
        Guid productId,
        DepartureTerms terms,
        DateTimeOffset cutoffAt,
        DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(productId, Guid.Empty);
        ArgumentNullException.ThrowIfNull(terms);

        var departure = new Departure
        {
            AgencyId = agencyId,
            ProductId = productId,
            Status = DepartureStatus.Open,
            Version = 1,
        };

        departure.Apply(terms.Normalised(), cutoffAt, now);
        departure.RefreshStatus();

        return departure;
    }

    /// <inheritdoc />
    public Guid AgencyId { get; private set; }

    public Guid ProductId { get; private set; }

    /// <summary>The day it leaves, in the agency's own calendar.</summary>
    public DateOnly DepartureDate { get; private set; }

    /// <summary>True when it only runs once <see cref="MinPax"/> travellers have paid.</summary>
    public bool IsGroupDeparture { get; private set; }

    public int MinPax { get; private set; }

    /// <summary>
    /// The largest party it takes. Equal to <see cref="CapacityTotal"/> until something needs them
    /// to differ — the build plan's own note on this table.
    /// </summary>
    public int MaxPax { get; private set; }

    public int CapacityTotal { get; private set; }

    /// <summary>Seats held during checkout, not yet paid. Moved only by the atomic seat update.</summary>
    public int CapacityReserved { get; private set; }

    /// <summary>Seats paid for. Moved only by the atomic seat update.</summary>
    public int CapacityConfirmed { get; private set; }

    public DepartureStatus Status { get; private set; }

    public DepositType DepositType { get; private set; }

    /// <summary>
    /// Set exactly when <see cref="DepositType"/> is <see cref="DepositType.Fixed"/>. The percentage
    /// deposit lives on <see cref="InstallmentPlan"/>, where the plan §2.5 puts it.
    /// </summary>
    public Money? DepositAmountMinor { get; private set; }

    /// <summary>Bookings close this many days before <see cref="DepartureDate"/>.</summary>
    public int CutoffDaysBefore { get; private set; }

    /// <summary>The instant bookings close, in UTC.</summary>
    public DateTimeOffset CutoffAt { get; private set; }

    /// <summary>Bumped by every edit the agent makes, and checked by the database on every save.</summary>
    public int Version { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>The price ladder, in party-size order.</summary>
    public IReadOnlyList<DeparturePriceTier> PriceTiers => _priceTiers;

    /// <summary>
    /// The deposit percentage and the payments the balance is split into. Always present once the
    /// departure has been created; nullable only because EF Core materialises it after the entity.
    /// </summary>
    public InstallmentPlan? Installments { get; private set; }

    /// <summary>Reserved plus confirmed, and what is left.</summary>
    public SeatCount Seats => new(CapacityTotal, CapacityReserved, CapacityConfirmed);

    /// <summary>The departure as terms again, every list in its stored order.</summary>
    public DepartureTerms ToTerms() => new()
    {
        DepartureDate = DepartureDate,
        IsGroupDeparture = IsGroupDeparture,
        MinPax = MinPax,
        CapacityTotal = CapacityTotal,
        CutoffDaysBefore = CutoffDaysBefore,
        DepositType = DepositType,
        DepositPercentBasisPoints = Installments?.DepositPercentBasisPoints,
        DepositAmountMinor = DepositAmountMinor,
        PriceTiers = [.. _priceTiers
            .OrderBy(tier => tier.MinPax)
            .Select(tier => new PriceTierTerms(tier.MinPax, tier.MaxPax, tier.PricePerPaxMinor))],
        Installments = [.. (Installments?.Items ?? [])
            .OrderBy(item => item.Sequence)
            .Select(item => new InstallmentTerms(
                item.Sequence, item.DueBasis, item.DueOffsetDays, item.PercentOfBalanceBasisPoints))],
    };

    /// <summary>
    /// Replaces everything the agent decides with <paramref name="terms"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The departure is cancelled — there is nothing left to edit — or the new capacity is below the
    /// seats already taken, which would leave travellers without a seat they have been sold.
    /// </exception>
    public void Revise(DepartureTerms terms, DateTimeOffset cutoffAt, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(terms);

        if (Status == DepartureStatus.Cancelled)
        {
            throw new InvalidOperationException("A cancelled departure cannot be changed.");
        }

        var taken = Seats.Taken;

        if (terms.CapacityTotal < taken)
        {
            throw new InvalidOperationException(
                $"{taken} seats are already taken. The capacity cannot go below {taken}.");
        }

        Apply(terms.Normalised(), cutoffAt, now);
        Version++;
        RefreshStatus();
    }

    /// <summary>Stops new bookings. The ones already made stand.</summary>
    /// <exception cref="InvalidOperationException">It is cancelled, or already closed.</exception>
    public void Close(DateTimeOffset now)
    {
        if (Status == DepartureStatus.Cancelled)
        {
            throw new InvalidOperationException("This departure is cancelled.");
        }

        if (Status == DepartureStatus.Closed)
        {
            throw new InvalidOperationException("This departure is already closed.");
        }

        Status = DepartureStatus.Closed;
        Version++;
        UpdatedAt = now;
    }

    /// <summary>Puts it back on sale, at whatever status its seats now give it.</summary>
    /// <exception cref="InvalidOperationException">It is not closed.</exception>
    public void Reopen(DateTimeOffset now)
    {
        if (Status != DepartureStatus.Closed)
        {
            throw new InvalidOperationException("This departure is not closed.");
        }

        Status = DepartureStatusRules.FromSeats(Seats, IsGroupDeparture, MinPax);
        Version++;
        UpdatedAt = now;
    }

    /// <summary>
    /// Calls it off. Every booking on it that has been paid for is refunded in full — build plan
    /// decision 12 — which the application does by putting each one on the resolution queue.
    /// </summary>
    /// <exception cref="InvalidOperationException">It is already cancelled.</exception>
    public void Cancel(DateTimeOffset now)
    {
        if (Status == DepartureStatus.Cancelled)
        {
            throw new InvalidOperationException("This departure is already cancelled.");
        }

        Status = DepartureStatus.Cancelled;
        Version++;
        UpdatedAt = now;
    }

    /// <summary>
    /// Brings the status back in line with the seats (job 9). Does nothing while the agent has
    /// closed or cancelled it: their decision outranks the arithmetic.
    /// </summary>
    /// <returns>True when the status changed.</returns>
    public bool RefreshStatus()
    {
        if (DepartureStatusRules.IsManual(Status))
        {
            return false;
        }

        var next = DepartureStatusRules.FromSeats(Seats, IsGroupDeparture, MinPax);

        if (next == Status)
        {
            return false;
        }

        Status = next;
        return true;
    }

    /// <summary>Whether a party of this size can still be sold seats today.</summary>
    public bool IsSellable(DateTimeOffset now, int paxCount) =>
        Status is not (DepartureStatus.Closed or DepartureStatus.Cancelled)
        && now < CutoffAt
        && Seats.SeatsLeft >= paxCount;

    private void Apply(DepartureTerms terms, DateTimeOffset cutoffAt, DateTimeOffset now)
    {
        DepartureDate = terms.DepartureDate;
        IsGroupDeparture = terms.IsGroupDeparture;
        MinPax = terms.MinPax;
        CapacityTotal = terms.CapacityTotal;
        MaxPax = terms.CapacityTotal;
        CutoffDaysBefore = terms.CutoffDaysBefore;
        CutoffAt = cutoffAt.ToUniversalTime();
        DepositType = terms.DepositType;
        DepositAmountMinor = terms.DepositAmountMinor;
        UpdatedAt = now;

        _priceTiers.Clear();
        _priceTiers.AddRange(terms.PriceTiers.Select(tier =>
            DeparturePriceTier.Create(AgencyId, Id, tier)));

        if (Installments is null)
        {
            Installments = InstallmentPlan.Create(AgencyId, Id, terms.DepositPercentBasisPoints, terms.Installments);
        }
        else
        {
            // Kept rather than replaced, so the plan's own id survives an edit and anything that
            // points at it — a booking's snapshot, an audit entry — still resolves.
            Installments.Replace(terms.DepositPercentBasisPoints, terms.Installments);
        }
    }
}
