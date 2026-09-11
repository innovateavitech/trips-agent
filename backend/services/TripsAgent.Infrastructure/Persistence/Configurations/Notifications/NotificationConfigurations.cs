using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TripsAgent.Domain.Notifications;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.Infrastructure.Persistence.Configurations.Notifications;

/// <summary>Maps <see cref="Notification"/> to <c>notifications.notifications</c>.</summary>
/// <remarks>
/// Tenant-scoped: the EF filter comes from <c>ITenantScoped</c> by convention, and the row-level
/// security policy from the <c>AddNotifications</c> migration.
/// </remarks>
public sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("notifications", Schemas.Notifications, table => table.HasCheckConstraint(
            "ck_notifications_status",
            $"status IN ({string.Join(", ", NotificationStatus.All.Select(status => $"'{status}'"))})"));

        builder.HasKey(notification => notification.Id);
        builder.Property(notification => notification.Id).ValueGeneratedNever();

        builder.Property(notification => notification.TemplateKey).HasMaxLength(100).IsRequired();
        builder.Property(notification => notification.Channel).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(notification => notification.Locale).HasMaxLength(20).IsRequired();
        builder.Property(notification => notification.RecipientType).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(notification => notification.RecipientAddress).HasMaxLength(320).IsRequired();
        builder.Property(notification => notification.RecipientName).HasMaxLength(200).IsRequired();
        builder.Property(notification => notification.Payload).HasColumnType("jsonb").IsRequired();
        builder.Property(notification => notification.DedupeKey).HasMaxLength(200).IsRequired();
        builder.Property(notification => notification.Status).HasMaxLength(20).IsRequired();
        builder.Property(notification => notification.ProviderMessageId).HasMaxLength(500);
        builder.Property(notification => notification.LastError).HasMaxLength(Notification.MaxErrorLength);

        // Ids, not files: the dispatcher reads each asset from storage when it sends. A native
        // uuid[] rather than jsonb, because every element is the same simple type.
        builder.Property(notification => notification.AttachmentAssetIds)
            .HasColumnType("uuid[]")
            .IsRequired();

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(notification => notification.AgencyId)
            .OnDelete(DeleteBehavior.Cascade);

        // The dedupe guarantee. Notifier checks first, so this only fires when two writers
        // race — and then it is what stops a job that ran three times from sending three emails.
        builder.HasIndex(notification => new { notification.AgencyId, notification.DedupeKey })
            .IsUnique()
            .HasDatabaseName("ix_notifications_agency_id_dedupe_key");

        // An agency's notification history, newest first.
        builder.HasIndex(notification => new { notification.AgencyId, notification.CreatedAt })
            .HasDatabaseName("ix_notifications_agency_id_created_at");

        // A bounce report arriving later names the provider's id, not ours.
        builder.HasIndex(notification => notification.ProviderMessageId)
            .HasDatabaseName("ix_notifications_provider_message_id")
            .HasFilter("provider_message_id IS NOT NULL");
    }
}

/// <summary>Maps <see cref="NotificationTemplate"/> to <c>notifications.notification_templates</c>.</summary>
public sealed class NotificationTemplateConfiguration : IEntityTypeConfiguration<NotificationTemplate>
{
    public void Configure(EntityTypeBuilder<NotificationTemplate> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("notification_templates", Schemas.Notifications);

        builder.HasKey(template => template.Id);
        builder.Property(template => template.Id).ValueGeneratedNever();

        builder.Property(template => template.Key).HasMaxLength(100).IsRequired();
        builder.Property(template => template.Channel).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(template => template.Locale).HasMaxLength(20).IsRequired();
        builder.Property(template => template.Audience).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(template => template.SubjectTemplate).HasMaxLength(500).IsRequired();
        builder.Property(template => template.HtmlTemplate).IsRequired();
        builder.Property(template => template.TextTemplate).IsRequired();

        builder.Ignore(template => template.IsActive);

        // A version is written once and never again; see NotificationTemplateSeeder.
        builder.HasIndex(template => new { template.Key, template.Channel, template.Locale, template.Version })
            .IsUnique()
            .HasDatabaseName("ix_notification_templates_key_channel_locale_version");
    }
}

/// <summary>Maps <see cref="SuppressedEmailAddress"/> to <c>notifications.suppressed_email_addresses</c>.</summary>
public sealed class SuppressedEmailAddressConfiguration : IEntityTypeConfiguration<SuppressedEmailAddress>
{
    public void Configure(EntityTypeBuilder<SuppressedEmailAddress> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("suppressed_email_addresses", Schemas.Notifications);

        builder.HasKey(suppression => suppression.Id);
        builder.Property(suppression => suppression.Id).ValueGeneratedNever();

        // citext, so Ada@Example.com and ada@example.com are the same mailbox — as they are to
        // every mail server that matters.
        builder.Property(suppression => suppression.Address).HasColumnType("citext").IsRequired();
        builder.Property(suppression => suppression.Reason).HasMaxLength(500).IsRequired();

        builder.Ignore(suppression => suppression.IsActive);

        // One active suppression per address. Partial, so a lifted one can be suppressed again if
        // the mailbox dies a second time, and the history of the first stays.
        builder.HasIndex(suppression => suppression.Address)
            .IsUnique()
            .HasDatabaseName("ix_suppressed_email_addresses_active")
            .HasFilter("lifted_at IS NULL");
    }
}
