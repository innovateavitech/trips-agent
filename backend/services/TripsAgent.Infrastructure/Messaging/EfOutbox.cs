using TripsAgent.Application.Messaging;
using TripsAgent.Infrastructure.Persistence;

namespace TripsAgent.Infrastructure.Messaging;

/// <summary>
/// <see cref="IOutbox"/> over the request's own <see cref="AppDbContext"/>. Adding to the same
/// context the handler saves is exactly what puts the message inside the handler's transaction.
/// </summary>
public sealed class EfOutbox(AppDbContext dbContext, TimeProvider clock) : IOutbox
{
    /// <inheritdoc />
    public void Enqueue<TMessage>(TMessage message, Guid? agencyId = null)
        where TMessage : class
    {
        ArgumentNullException.ThrowIfNull(message);

        dbContext.OutboxMessages.Add(OutboxMessage.Create(message, agencyId, clock.GetUtcNow()));
    }
}
