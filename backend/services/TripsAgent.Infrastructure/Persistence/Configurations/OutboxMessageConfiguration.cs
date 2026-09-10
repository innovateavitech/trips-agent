using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TripsAgent.Domain.Messaging;

namespace TripsAgent.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="OutboxMessage"/> onto <c>platform.outbox_messages</c>.</summary>
internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    /// <summary>Every table the plan puts under "audit &amp; operations" lives in this schema.</summary>
    public const string Schema = "platform";

    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("outbox_messages", Schema);

        builder.HasKey(message => message.Id);

        builder.Property(message => message.MessageType).HasMaxLength(500).IsRequired();
        builder.Property(message => message.PayloadJson).HasColumnType("jsonb").IsRequired();
        builder.Property(message => message.CorrelationId).HasMaxLength(100);
        builder.Property(message => message.LastError).HasMaxLength(OutboxMessage.MaxErrorLength);

        // Partial: only rows the dispatcher will ever look at. Once a row dispatches it never
        // matches this index again, so the index stays the size of the backlog, not the size of
        // history — the dispatcher's query is fast on day one and still fast a year in.
        builder
            .HasIndex(message => new { message.OccurredAt, message.Id })
            .HasDatabaseName("ix_outbox_messages_pending")
            .HasFilter("dispatched_at IS NULL");

        builder
            .HasIndex(message => new { message.AgencyId, message.OccurredAt })
            .HasDatabaseName("ix_outbox_messages_agency");
    }
}
