using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Catalog;

// The rows a departure is made of. Each carries its own agency_id rather than relying on being
// reachable through departure_id: that is what makes every one of them ITenantScoped, so the EF
// filter and the row-level security policy apply to each table directly (ADR-0006).

/// <summary>The price per traveller for a party of this size.</summary>
public sealed class DeparturePriceTier : Entity, ITenantScoped
{
    private DeparturePriceTier()
    {
    }

    internal static DeparturePriceTier Create(Guid agencyId, Guid departureId, PriceTierTerms terms) =>
        new()
        {
            AgencyId = agencyId,
            DepartureId = departureId,
            MinPax = terms.MinPax,
            MaxPax = terms.MaxPax,
            PricePerPaxMinor = terms.PricePerPaxMinor,
        };

    /// <inheritdoc />
    public Guid AgencyId { get; private set; }

    public Guid DepartureId { get; private set; }

    /// <summary>The smallest party this price is for. 1 on the first tier.</summary>
    public int MinPax { get; private set; }

    /// <summary>The largest. Null on the last tier: this size and up.</summary>
    public int? MaxPax { get; private set; }

    public Money PricePerPaxMinor { get; private set; }
}

/// <summary>
/// How a departure is paid for: the deposit percentage, and the payments the balance is split into.
/// </summary>
/// <remarks>
/// One per departure. The plan §2.5 puts <c>deposit_percent</c> here and <c>deposit_type</c> and
/// <c>deposit_amount_minor</c> on the departure itself, so a percentage deposit is read here and a
/// fixed one there. <see cref="DepartureTerms"/> puts them back together for everyone else.
/// </remarks>
public sealed class InstallmentPlan : Entity, ITenantScoped
{
    private readonly List<InstallmentScheduleItem> _items = [];

    private InstallmentPlan()
    {
    }

    internal static InstallmentPlan Create(
        Guid agencyId,
        Guid departureId,
        int? depositPercentBasisPoints,
        IReadOnlyList<InstallmentTerms> items)
    {
        var plan = new InstallmentPlan
        {
            AgencyId = agencyId,
            DepartureId = departureId,
        };

        plan.Replace(depositPercentBasisPoints, items);

        return plan;
    }

    /// <inheritdoc />
    public Guid AgencyId { get; private set; }

    public Guid DepartureId { get; private set; }

    /// <summary>The deposit as a share of the seat price, in basis points. Null unless the deposit is a percentage.</summary>
    public int? DepositPercentBasisPoints { get; private set; }

    /// <summary>The payments, in sequence. Empty means the balance is due at the cutoff.</summary>
    public IReadOnlyList<InstallmentScheduleItem> Items => _items;

    internal void Replace(int? depositPercentBasisPoints, IReadOnlyList<InstallmentTerms> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        DepositPercentBasisPoints = depositPercentBasisPoints;

        _items.Clear();
        _items.AddRange(items.Select(item => InstallmentScheduleItem.Create(AgencyId, Id, item)));
    }
}

/// <summary>One payment of the balance, as an offset rather than a date: it is a template, not a bill.</summary>
public sealed class InstallmentScheduleItem : Entity, ITenantScoped
{
    private InstallmentScheduleItem()
    {
    }

    internal static InstallmentScheduleItem Create(Guid agencyId, Guid planId, InstallmentTerms terms) =>
        new()
        {
            AgencyId = agencyId,
            InstallmentPlanId = planId,
            Sequence = terms.Sequence,
            DueBasis = terms.DueBasis,
            DueOffsetDays = terms.DueOffsetDays,
            PercentOfBalanceBasisPoints = terms.PercentOfBalanceBasisPoints,
        };

    /// <inheritdoc />
    public Guid AgencyId { get; private set; }

    public Guid InstallmentPlanId { get; private set; }

    /// <summary>1 for the first payment.</summary>
    public int Sequence { get; private set; }

    public InstallmentDueBasis DueBasis { get; private set; }

    public int DueOffsetDays { get; private set; }

    /// <summary>A share of the balance in basis points. Every item adds up to 10,000.</summary>
    public int PercentOfBalanceBasisPoints { get; private set; }
}

/// <summary>
/// Seats held for one cart while its traveller checks out, and given back when they do not.
/// </summary>
/// <remarks>
/// A hold is what turns "somebody is buying this" into a seat nobody else can have. It counts
/// against <see cref="Departure.CapacityReserved"/> until it is converted or released, and
/// <c>CartAndHoldExpiryJob</c> (plan §3, job 6) releases the ones that time out.
/// </remarks>
public sealed class DepartureHold : Entity, ITenantScoped
{
    private DepartureHold()
    {
    }

    public static DepartureHold Place(
        Guid agencyId,
        Guid departureId,
        Guid cartId,
        int paxCount,
        DateTimeOffset now,
        TimeSpan timeToLive)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(departureId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(cartId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfLessThan(paxCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeToLive, TimeSpan.Zero);

        return new DepartureHold
        {
            AgencyId = agencyId,
            DepartureId = departureId,
            CartId = cartId,
            PaxCount = paxCount,
            Status = DepartureHoldStatus.Held,
            ExpiresAt = now.ToUniversalTime() + timeToLive,
            CreatedAt = now.ToUniversalTime(),
        };
    }

    /// <inheritdoc />
    public Guid AgencyId { get; private set; }

    public Guid DepartureId { get; private set; }

    /// <summary>The cart these seats are being bought in.</summary>
    public Guid CartId { get; private set; }

    public int PaxCount { get; private set; }

    public DepartureHoldStatus Status { get; private set; }

    /// <summary>When the seats go back on sale unless the hold is converted first.</summary>
    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>When it stopped being held, either way. Null while it is still held.</summary>
    public DateTimeOffset? SettledAt { get; private set; }

    /// <summary>Gives the seats back. Idempotent: releasing a settled hold does nothing.</summary>
    /// <returns>True when this call is the one that released it.</returns>
    public bool Release(DateTimeOffset now)
    {
        if (Status != DepartureHoldStatus.Held)
        {
            return false;
        }

        Status = DepartureHoldStatus.Released;
        SettledAt = now.ToUniversalTime();

        return true;
    }

    /// <summary>Turns the held seats into paid ones. Idempotent, for the same reason.</summary>
    /// <returns>True when this call is the one that converted it.</returns>
    public bool Convert(DateTimeOffset now)
    {
        if (Status != DepartureHoldStatus.Held)
        {
            return false;
        }

        Status = DepartureHoldStatus.Converted;
        SettledAt = now.ToUniversalTime();

        return true;
    }
}

/// <summary>
/// Someone waiting for a seat on a full departure (FRD §2.13 RS-6).
/// </summary>
/// <remarks>
/// When a seat frees up the first person waiting is offered it for a fixed time; when the offer
/// runs out <c>WaitlistOfferExpiryJob</c> (plan §3, job 10) rolls it on to the next person.
/// </remarks>
public sealed class DepartureWaitlistEntry : Entity, ITenantScoped
{
    private DepartureWaitlistEntry()
    {
        Name = string.Empty;
        Email = string.Empty;
    }

    public static DepartureWaitlistEntry Join(
        Guid agencyId,
        Guid departureId,
        string name,
        string email,
        int paxCount,
        DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(departureId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentOutOfRangeException.ThrowIfLessThan(paxCount, 1);

        return new DepartureWaitlistEntry
        {
            AgencyId = agencyId,
            DepartureId = departureId,
            Name = name.Trim(),
            Email = email.Trim(),
            PaxCount = paxCount,
            Status = WaitlistStatus.Waiting,
            JoinedAt = now.ToUniversalTime(),
        };
    }

    /// <inheritdoc />
    public Guid AgencyId { get; private set; }

    public Guid DepartureId { get; private set; }

    /// <summary>Who is waiting, as they gave it.</summary>
    public string Name { get; private set; }

    /// <summary>Where the offer is sent. Waitlist offers are email only in the MVP.</summary>
    public string Email { get; private set; }

    public int PaxCount { get; private set; }

    public WaitlistStatus Status { get; private set; }

    /// <summary>Their place in the queue: the earliest waiting entry is offered first.</summary>
    public DateTimeOffset JoinedAt { get; private set; }

    public DateTimeOffset? OfferedAt { get; private set; }

    /// <summary>When the offer runs out. Null unless an offer is open.</summary>
    public DateTimeOffset? ExpiresAt { get; private set; }

    /// <summary>Offers them the seats for <paramref name="timeToLive"/>.</summary>
    /// <exception cref="InvalidOperationException">They are not waiting — already offered, converted or expired.</exception>
    public void Offer(DateTimeOffset now, TimeSpan timeToLive)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeToLive, TimeSpan.Zero);

        if (Status != WaitlistStatus.Waiting)
        {
            throw new InvalidOperationException($"A {Status} waitlist entry cannot be offered a seat.");
        }

        Status = WaitlistStatus.Offered;
        OfferedAt = now.ToUniversalTime();
        ExpiresAt = now.ToUniversalTime() + timeToLive;
    }

    /// <summary>The offer ran out, or they withdrew. Idempotent.</summary>
    /// <returns>True when this call is the one that expired it.</returns>
    public bool Expire()
    {
        if (Status is WaitlistStatus.Converted or WaitlistStatus.Expired)
        {
            return false;
        }

        Status = WaitlistStatus.Expired;
        ExpiresAt = null;

        return true;
    }

    /// <summary>They took the seats.</summary>
    public void Convert()
    {
        Status = WaitlistStatus.Converted;
        ExpiresAt = null;
    }
}

/// <summary>
/// One traveller on one departure: who is going, on which booking, and which room they are in.
/// </summary>
/// <remarks>
/// The manifest is what an agent hands the operator on the day. The traveller's name and type come
/// from the order line's travellers; this row adds the departure they are on and the room they
/// share (FRD §2.13 UC-1B RS-4).
/// </remarks>
public sealed class PaxManifestEntry : Entity, IAuditableEntity, ITenantScoped
{
    private PaxManifestEntry()
    {
    }

    public static PaxManifestEntry Create(
        Guid agencyId,
        Guid departureId,
        Guid orderLineId,
        Guid orderTravellerId,
        string? roomAssignment,
        DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(departureId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(orderLineId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(orderTravellerId, Guid.Empty);

        return new PaxManifestEntry
        {
            AgencyId = agencyId,
            DepartureId = departureId,
            OrderLineId = orderLineId,
            OrderTravellerId = orderTravellerId,
            RoomAssignment = Tidy(roomAssignment),
            CreatedAt = now.ToUniversalTime(),
            UpdatedAt = now.ToUniversalTime(),
        };
    }

    /// <inheritdoc />
    public Guid AgencyId { get; private set; }

    public Guid DepartureId { get; private set; }

    /// <summary>The order line that bought this seat.</summary>
    public Guid OrderLineId { get; private set; }

    /// <summary>The traveller on that line.</summary>
    public Guid OrderTravellerId { get; private set; }

    /// <summary>"Room 3", "Twin share". Null until rooms are assigned.</summary>
    public string? RoomAssignment { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Puts this traveller in a room, or takes them out of one.</summary>
    public void AssignRoom(string? roomAssignment, DateTimeOffset now)
    {
        RoomAssignment = Tidy(roomAssignment);
        UpdatedAt = now.ToUniversalTime();
    }

    private static string? Tidy(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
