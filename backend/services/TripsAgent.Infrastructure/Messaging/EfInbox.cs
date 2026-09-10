using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using TripsAgent.Application.Messaging;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.Infrastructure.Persistence.Configurations;

namespace TripsAgent.Infrastructure.Messaging;

/// <summary><see cref="IInbox"/> over <c>platform.inbox_messages</c>.</summary>
public sealed partial class EfInbox(AppDbContext dbContext, TimeProvider clock, ILogger<EfInbox> logger) : IInbox
{
    /// <inheritdoc />
    public async Task<bool> ProcessOnceAsync(
        Guid messageId,
        string consumer,
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumer);
        ArgumentNullException.ThrowIfNull(work);

        // The cheap, common case first: a redelivery of something finished long ago. No point doing
        // the work only for the insert below to throw it away.
        var alreadyProcessed = await dbContext.InboxMessages
            .AnyAsync(m => m.MessageId == messageId && m.Consumer == consumer, cancellationToken);

        if (alreadyProcessed)
        {
            LogDuplicateSkipped(logger, messageId, consumer);
            return false;
        }

        await work(cancellationToken);

        dbContext.InboxMessages.Add(new InboxMessage(messageId, consumer, clock.GetUtcNow()));

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (IsDuplicateDelivery(ex))
        {
            // Two copies arrived together and both passed the check above. The other one committed
            // first, so PostgreSQL rejected our inbox row — and, because it is one transaction,
            // everything the work staged along with it. Nothing happened twice.
            //
            // Cleared so the rejected changes are not retried by the next save on this context.
            dbContext.ChangeTracker.Clear();
            LogDuplicateSkipped(logger, messageId, consumer);
            return false;
        }
    }

    // Only a clash on the inbox's own key means "duplicate". A unique violation anywhere else — the
    // work inserting a second account with the same email, say — is a real error and must surface.
    private static bool IsDuplicateDelivery(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: InboxMessageConfiguration.PrimaryKeyName,
        };

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Message {MessageId} was already processed by {Consumer}; skipped the duplicate.")]
    private static partial void LogDuplicateSkipped(ILogger logger, Guid messageId, string consumer);
}
