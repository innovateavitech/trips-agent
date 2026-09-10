namespace TripsAgent.Application.Messaging;

/// <summary>
/// Stages a message to be published after the current unit of work commits.
/// </summary>
/// <remarks>
/// <para>
/// Calling <see cref="Enqueue{TMessage}"/> sends nothing. It adds a row to
/// <c>platform.outbox_messages</c> in the same pending database transaction as your other
/// changes, and that row only becomes real when your handler saves. If the save fails, the
/// message was never queued. If it succeeds, the Worker publishes it within a few seconds — even
/// if this process dies the instant after the commit.
/// </para>
/// <para>
/// Usually you do not need this interface at all: raise a domain event from the aggregate with
/// <c>AggregateRoot.Raise</c> and it is staged for you on save. Reach for <see cref="IOutbox"/>
/// when what happened does not belong to a single aggregate.
/// </para>
/// </remarks>
public interface IOutbox
{
    /// <summary>Stages <paramref name="message"/> for publishing once the caller saves.</summary>
    /// <typeparam name="TMessage">A record of plain, JSON-serialisable data.</typeparam>
    /// <param name="message">The message. Named in the past tense if it is an event.</param>
    /// <param name="agencyId">
    /// The agency the message concerns, if any. Carried for routing and for logs; it is not a
    /// security boundary.
    /// </param>
    public void Enqueue<TMessage>(TMessage message, Guid? agencyId = null)
        where TMessage : class;
}
