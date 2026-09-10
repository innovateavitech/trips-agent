namespace TripsAgent.Application.Messaging;

/// <summary>
/// Stages an event to be published, as part of whatever business transaction is already in
/// progress.
/// </summary>
/// <remarks>
/// <para>
/// This is the application-facing half of the outbox pattern (issue #30). A handler that changes
/// state and needs the rest of the platform to know calls <see cref="Enqueue{TEvent}"/> and then
/// saves its own unit of work exactly as it already does — the event rides along in the same
/// <c>SaveChanges</c> call as the state change, so both commit together or neither does.
/// </para>
/// <para>
/// This is deliberately <em>not</em> <see cref="IMessageBus"/>. Publishing straight to the broker
/// from inside a handler would mean the event can go out before the transaction that produced it
/// has committed — or after it committed but the handler then failed for an unrelated reason and
/// rolled back, leaving an event on the broker describing a change that never happened. Queuing
/// it here and letting a separate dispatcher publish it once the row is safely committed removes
/// that whole class of bug.
/// </para>
/// </remarks>
public interface IOutboxWriter
{
    /// <summary>
    /// Queues <paramref name="domainEvent"/> to be published once the current unit of work
    /// commits. Does not touch the database itself — it stages the row on the ambient
    /// <c>DbContext</c>, exactly like adding any other entity, so the caller's own
    /// <c>SaveChangesAsync</c> is what actually writes it.
    /// </summary>
    /// <typeparam name="TEvent">The event's compile-time type. Its actual runtime type is what gets dispatched, so publishing through a base type still dispatches as the real subtype.</typeparam>
    /// <param name="domainEvent">The event. Must be a class — a struct will not serialise usefully.</param>
    /// <param name="agencyId">The agency this event belongs to, or null for a platform-wide event.</param>
    /// <param name="correlationId">Ties this event to the request or job that produced it.</param>
    public void Enqueue<TEvent>(TEvent domainEvent, Guid? agencyId = null, string? correlationId = null)
        where TEvent : class;
}
