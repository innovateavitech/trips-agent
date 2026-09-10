using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TripsAgent.Infrastructure.Messaging;

namespace TripsAgent.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="OutboxMessage"/> to <c>platform.outbox_messages</c>.</summary>
/// <remarks>
/// No tenant query filter, deliberately. The outbox is platform plumbing: the dispatcher has to see
/// every agency's pending messages in one query. <c>agency_id</c> is here so a stuck message can be
/// traced to the agency it affects, not to scope reads.
/// </remarks>
public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    // Written as literals, not parameters, so a query with the same literal predicate can use the
    // partial index. See OutboxDispatcher.
    private const string PendingOnly = "status = '" + OutboxMessageStatus.Pending + "'";
    private const string NotYetDispatched = "status <> '" + OutboxMessageStatus.Dispatched + "'";

    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("outbox_messages", Schemas.Platform, table => table.HasCheckConstraint(
            "ck_outbox_messages_status",
            $"status IN ('{OutboxMessageStatus.Pending}', '{OutboxMessageStatus.Dispatched}', '{OutboxMessageStatus.Failed}')"));

        builder.HasKey(m => m.Id);

        // The id is the message id consumers deduplicate on, so it is always the one the
        // application generated — never one the database makes up.
        builder.Property(m => m.Id).ValueGeneratedNever();

        builder.Property(m => m.MessageType).HasMaxLength(500);
        builder.Property(m => m.Payload).HasColumnType("jsonb");
        builder.Property(m => m.CorrelationId).HasMaxLength(200);
        builder.Property(m => m.Status).HasMaxLength(20);
        builder.Property(m => m.LastError).HasMaxLength(OutboxMessage.MaxErrorLength);

        // The dispatcher's question every couple of seconds: "what is due?". Partial, so the index
        // holds only pending rows and stays tiny however many millions have been dispatched.
        builder.HasIndex(m => m.NextAttemptAt)
            .HasDatabaseName("ix_outbox_messages_due")
            .HasFilter(PendingOnly);

        // The backlog monitor's question: "how many are waiting or stuck, and since when?".
        builder.HasIndex(m => new { m.Status, m.OccurredAt })
            .HasDatabaseName("ix_outbox_messages_undispatched")
            .HasFilter(NotYetDispatched);
    }
}
