using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Messaging;
using TripsAgent.Infrastructure.Persistence;

namespace TripsAgent.Infrastructure.Messaging;

/// <summary>
/// Checks and records consumer-side delivery, via a single <c>INSERT … ON CONFLICT DO NOTHING</c>.
/// See <see cref="IInboxDeduplicator"/>.
/// </summary>
/// <remarks>
/// Raw SQL rather than <c>DbSet.Add</c> plus catching a unique-violation exception. A redelivery
/// is an expected, routine event under at-least-once delivery — not an error — and driving normal
/// control flow through a caught <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>
/// would be both slower and a strange thing to call unexceptional. <c>ON CONFLICT DO NOTHING</c>
/// makes "was this new" an ordinary return value: 1 row affected, or 0.
///
/// This also runs independently of whatever else is tracked on <see cref="AppDbContext"/> — it is
/// its own statement, not part of the caller's pending changes — so a consumer can call it before
/// starting its own unit of work without the two interfering.
/// </remarks>
public sealed class InboxDeduplicator(AppDbContext context, TimeProvider timeProvider) : IInboxDeduplicator
{
    /// <inheritdoc />
    public async Task<bool> TryBeginProcessingAsync(
        Guid messageId,
        string consumerName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerName);

        var now = timeProvider.GetUtcNow();

        var rowsInserted = await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO platform.inbox_messages (message_id, consumer_name, processed_at)
             VALUES ({messageId}, {consumerName}, {now})
             ON CONFLICT (message_id, consumer_name) DO NOTHING
             """,
            cancellationToken);

        return rowsInserted == 1;
    }
}
