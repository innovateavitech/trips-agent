using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TripsAgent.Domain.Messaging;

namespace TripsAgent.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="InboxMessage"/> onto <c>platform.inbox_messages</c>.</summary>
internal sealed class InboxMessageConfiguration : IEntityTypeConfiguration<InboxMessage>
{
    public void Configure(EntityTypeBuilder<InboxMessage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("inbox_messages", OutboxMessageConfiguration.Schema);

        // The composite key is the dedupe: the same (message, consumer) pair can exist once,
        // full stop. InboxDeduplicator relies on this exact constraint name and column order
        // for its ON CONFLICT clause.
        builder.HasKey(message => new { message.MessageId, message.ConsumerName });

        builder.Property(message => message.ConsumerName).HasMaxLength(InboxMessage.MaxConsumerNameLength).IsRequired();
    }
}
