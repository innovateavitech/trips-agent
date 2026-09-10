using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TripsAgent.Infrastructure.Messaging;

namespace TripsAgent.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="InboxMessage"/> to <c>platform.inbox_messages</c>.</summary>
public sealed class InboxMessageConfiguration : IEntityTypeConfiguration<InboxMessage>
{
    /// <summary>
    /// Named explicitly because <see cref="EfInbox"/> matches on it: a violation of <em>this</em>
    /// key means "duplicate message", and any other unique violation is a real error.
    /// </summary>
    public const string PrimaryKeyName = "pk_inbox_messages";

    public void Configure(EntityTypeBuilder<InboxMessage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("inbox_messages", Schemas.Platform);

        // The primary key is the deduplication. PostgreSQL enforces it inside the transaction, so
        // two copies of one message racing through two Workers cannot both commit.
        builder.HasKey(m => new { m.MessageId, m.Consumer }).HasName(PrimaryKeyName);

        builder.Property(m => m.Consumer).HasMaxLength(200);
    }
}
