namespace TripsAgent.Domain.Common;

/// <summary>
/// A fact about something that happened in the domain — <c>OrderPlaced</c>,
/// <c>WalletToppedUp</c>, <c>BookingTicketed</c> — that other parts of the system may react to.
/// </summary>
/// <remarks>
/// <para>
/// Name events in the past tense and make them <c>record</c>s of plain data: ids, amounts in
/// minor units, <see cref="DateTimeOffset"/>s. They are serialised to JSON and kept in
/// <c>platform.outbox_messages</c> until the Worker publishes them, so anything that does not
/// survive a round trip through JSON — a live <c>DbContext</c>, a <c>Stream</c>, a lambda —
/// does not belong in one.
/// </para>
/// <para>
/// Raise one from an aggregate with <see cref="AggregateRoot.Raise"/>. It is written to the
/// outbox in the same database transaction as the change that caused it: either both commit or
/// neither does. See docs/adr/0005-own-the-transactional-outbox.md.
/// </para>
/// </remarks>
public interface IDomainEvent
{
}

/// <summary>
/// An entity that collects domain events for <c>AppDbContext</c> to move into the outbox when it
/// saves. Inherit <see cref="AggregateRoot"/> rather than implementing this by hand.
/// </summary>
public interface IHasDomainEvents
{
    /// <summary>Returns every event raised since the last call, and forgets them.</summary>
    /// <remarks>
    /// Returning and clearing in one step means two saves of the same context can never pick up
    /// the same event twice. It is a method rather than a property on purpose: EF Core tries to
    /// map every public property to a column, and a list of events is not a column.
    /// </remarks>
    public IReadOnlyList<IDomainEvent> PullDomainEvents();
}

/// <summary>
/// Base class for an entity that is the entry point to a cluster of related objects — an order
/// with its lines, a wallet with its holds — and that announces what happens to it.
/// </summary>
/// <remarks>
/// Call <see cref="Raise"/> from inside the method that changes state:
/// <code>
/// public void Place()
/// {
///     Status = OrderStatus.Placed;
///     Raise(new OrderPlaced(Id, AgencyId, TotalMinor));
/// }
/// </code>
/// Nothing is sent at that moment. The event waits on the aggregate until
/// <c>SaveChangesAsync</c>, which writes it to the outbox alongside the status change.
/// </remarks>
public abstract class AggregateRoot : Entity, IHasDomainEvents
{
    private readonly List<IDomainEvent> _pendingEvents = [];

    /// <summary>Creates an aggregate with a freshly generated, time-ordered identity.</summary>
    protected AggregateRoot()
    {
    }

    /// <summary>Rehydration constructor for EF Core and for tests that need a deterministic id.</summary>
    protected AggregateRoot(Guid id)
        : base(id)
    {
    }

    /// <inheritdoc />
    public IReadOnlyList<IDomainEvent> PullDomainEvents()
    {
        var events = _pendingEvents.ToArray();
        _pendingEvents.Clear();
        return events;
    }

    /// <summary>Records that something happened. It is published after the next successful save.</summary>
    protected void Raise(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        _pendingEvents.Add(domainEvent);
    }
}
