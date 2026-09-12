using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TripsAgent.Domain.Catalog;
using TripsAgent.Domain.Crm;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.Infrastructure.Persistence.Configurations.Crm;

/// <summary>Shared constants for the CRM tables.</summary>
public static class CrmSchema
{
    /// <summary>The PostgreSQL schema holding each agency's customers, leads, quotes, tasks and messages.</summary>
    public const string Name = "crm";
}

/// <summary>
/// <c>crm.customers</c>. The CHECKs, row-level security and grants are in the migration, as
/// hand-written SQL: EF Core can express none of them.
/// </summary>
/// <remarks>
/// <para>
/// Every reference between CRM tables is a <b>composite</b> foreign key — <c>(agency_id, customer_id)</c>
/// against <c>(agency_id, id)</c> — rather than a plain one. A plain key would accept another agency's
/// customer id: foreign-key checks run as the table owner and skip row-level security. Pairing the id
/// with the agency makes the database itself refuse a lead, a task or a booking that points across
/// agencies. The catalog does the same (see <c>ProductConfiguration</c>).
/// </para>
/// <para>
/// Nothing in the CRM is ever deleted by the application: the role has no DELETE on the customers,
/// leads, quotes, tasks or messages. Erasing a person (#106) anonymises this row in place.
/// </para>
/// </remarks>
public sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("customers", CrmSchema.Name);
        builder.HasKey(customer => customer.Id);
        builder.Property(customer => customer.Id).ValueGeneratedNever();

        builder.Property(customer => customer.Name).HasMaxLength(CrmLimits.MaxNameLength).IsRequired();
        builder.Property(customer => customer.Email).HasMaxLength(CrmLimits.MaxEmailLength);
        builder.Property(customer => customer.Phone).HasMaxLength(CrmLimits.MaxPhoneLength);
        builder.Property(customer => customer.PhoneKey).HasMaxLength(CrmLimits.MaxPhoneLength);

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(customer => customer.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        // One customer per email within an agency: the lookup every inquiry starts with, and the
        // database's word that two requests racing to create the same person make only one.
        builder.HasIndex(customer => new { customer.AgencyId, customer.Email })
            .IsUnique()
            .HasFilter("email IS NOT NULL")
            .HasDatabaseName("ix_customers_agency_id_email");

        // Not unique: a family, or an office, can share a phone.
        builder.HasIndex(customer => new { customer.AgencyId, customer.PhoneKey })
            .HasFilter("phone_key IS NOT NULL")
            .HasDatabaseName("ix_customers_agency_id_phone_key");

        // The customer list, most recently active first.
        builder.HasIndex(customer => new { customer.AgencyId, customer.LastActivityAt })
            .HasDatabaseName("ix_customers_agency_id_last_activity_at");

        // The booking's customer, for the customer 360 (FRD §2.8 RS-1). orders.customer_id has been
        // waiting for this table since #41; configured from this side so the orders' own
        // configuration stays as it was.
        builder.HasMany<Order>()
            .WithOne()
            .HasForeignKey(order => new { order.AgencyId, order.CustomerId })
            .HasPrincipalKey(customer => new { customer.AgencyId, customer.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_orders_customers_customer");
    }
}

/// <summary><c>crm.leads</c>: inquiries, and where each has got to.</summary>
public sealed class LeadConfiguration : IEntityTypeConfiguration<Lead>
{
    public void Configure(EntityTypeBuilder<Lead> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("leads", CrmSchema.Name);
        builder.HasKey(lead => lead.Id);
        builder.Property(lead => lead.Id).ValueGeneratedNever();

        // Stored by name, not number, so a psql query reads "Quoted" rather than "2".
        builder.Property(lead => lead.Source).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(lead => lead.Stage).HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.Property(lead => lead.Destination).HasMaxLength(CrmLimits.MaxDestinationLength).IsRequired();
        builder.Property(lead => lead.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(lead => lead.Message).HasMaxLength(CrmLimits.MaxMessageLength).IsRequired();
        builder.Property(lead => lead.LostReason).HasMaxLength(CrmLimits.MaxReasonLength);

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(lead => lead.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Customer>()
            .WithMany()
            .HasForeignKey(lead => new { lead.AgencyId, lead.CustomerId })
            .HasPrincipalKey(customer => new { customer.AgencyId, customer.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(lead => lead.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // Restrict, not cascade: the history is a record, and leads are never deleted.
        builder.HasMany(lead => lead.History)
            .WithOne()
            .HasForeignKey(change => change.LeadId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(lead => lead.History).UsePropertyAccessMode(PropertyAccessMode.Field);

        // The pipeline board: every lead of one agency, newest first.
        builder.HasIndex(lead => new { lead.AgencyId, lead.CreatedAt })
            .HasDatabaseName("ix_leads_agency_id_created_at");
    }
}

/// <summary><c>crm.lead_stage_history</c>: every move of every lead. Append-only for the application role.</summary>
public sealed class LeadStageChangeConfiguration : IEntityTypeConfiguration<LeadStageChange>
{
    public void Configure(EntityTypeBuilder<LeadStageChange> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("lead_stage_history", CrmSchema.Name);
        builder.HasKey(change => change.Id);
        builder.Property(change => change.Id).ValueGeneratedNever();

        builder.Property(change => change.Stage).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(change => change.Reason).HasMaxLength(CrmLimits.MaxReasonLength);
        builder.Property(change => change.ByName).HasMaxLength(CrmLimits.MaxNameLength).IsRequired();

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(change => change.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(change => new { change.AgencyId, change.LeadId, change.At })
            .HasDatabaseName("ix_lead_stage_history_agency_id_lead_id_at");
    }
}

/// <summary><c>crm.quotes</c>.</summary>
public sealed class QuoteConfiguration : IEntityTypeConfiguration<Quote>
{
    public void Configure(EntityTypeBuilder<Quote> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("quotes", CrmSchema.Name);
        builder.HasKey(quote => quote.Id);
        builder.Property(quote => quote.Id).ValueGeneratedNever();

        builder.Property(quote => quote.QuoteNumber).HasMaxLength(20).IsRequired();
        builder.Property(quote => quote.Title).HasMaxLength(CrmLimits.MaxQuoteTitleLength).IsRequired();
        builder.Property(quote => quote.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(quote => quote.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(quote => quote.Notes).HasMaxLength(CrmLimits.MaxNotesLength).IsRequired();
        builder.Property(quote => quote.PublicToken).HasMaxLength(64);

        // Optimistic concurrency. A save of a draft that races the send of the same quote must not
        // win: the customer would be holding a quote that changed after it was sent. Both bump the
        // version, so whichever commits second is refused and told to reload.
        builder.Property(quote => quote.Version).IsConcurrencyToken().IsRequired();

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(quote => quote.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Lead>()
            .WithMany()
            .HasForeignKey(quote => new { quote.AgencyId, quote.LeadId })
            .HasPrincipalKey(lead => new { lead.AgencyId, lead.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // The items and days are part of the quote and are replaced when a draft is saved.
        builder.HasMany(quote => quote.Items)
            .WithOne()
            .HasForeignKey(item => item.QuoteId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(quote => quote.Items).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(quote => quote.Itinerary)
            .WithOne()
            .HasForeignKey(day => day.QuoteId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(quote => quote.Itinerary).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Numbers run per agency: two agencies both have a QT-0001.
        builder.HasIndex(quote => new { quote.AgencyId, quote.Number })
            .IsUnique()
            .HasDatabaseName("ix_quotes_agency_id_number");

        // The customer's link. Looked up by token, always inside the agency its host resolved to.
        builder.HasIndex(quote => quote.PublicToken)
            .IsUnique()
            .HasFilter("public_token IS NOT NULL")
            .HasDatabaseName("ix_quotes_public_token");
    }
}

/// <summary><c>crm.quote_items</c>: a quote's priced lines, in order.</summary>
public sealed class QuoteItemConfiguration : IEntityTypeConfiguration<QuoteItem>
{
    public void Configure(EntityTypeBuilder<QuoteItem> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("quote_items", CrmSchema.Name);
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).ValueGeneratedNever();

        builder.Property(item => item.Description).HasMaxLength(CrmLimits.MaxItemDescriptionLength).IsRequired();

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(item => item.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        // Composite, so a quote can only sell the agency's own product.
        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(item => new { item.AgencyId, item.ProductId })
            .HasPrincipalKey(product => new { product.AgencyId, product.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(item => new { item.QuoteId, item.Position })
            .IsUnique()
            .HasDatabaseName("ix_quote_items_quote_id_position");
    }
}

/// <summary><c>crm.quote_itinerary_days</c>: day 1, day 2… of a quote.</summary>
public sealed class QuoteItineraryDayConfiguration : IEntityTypeConfiguration<QuoteItineraryDay>
{
    public void Configure(EntityTypeBuilder<QuoteItineraryDay> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("quote_itinerary_days", CrmSchema.Name);
        builder.HasKey(day => day.Id);
        builder.Property(day => day.Id).ValueGeneratedNever();

        builder.Property(day => day.Title).HasMaxLength(CrmLimits.MaxDayTitleLength).IsRequired();
        builder.Property(day => day.Description).HasMaxLength(CrmLimits.MaxDayDescriptionLength).IsRequired();

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(day => day.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(day => new { day.QuoteId, day.DayNumber })
            .IsUnique()
            .HasDatabaseName("ix_quote_itinerary_days_quote_id_day_number");

        builder.HasIndex(day => day.AgencyId).HasDatabaseName("ix_quote_itinerary_days_agency_id");
    }
}

/// <summary><c>crm.tasks</c>: follow-up tasks.</summary>
public sealed class FollowUpTaskConfiguration : IEntityTypeConfiguration<FollowUpTask>
{
    public void Configure(EntityTypeBuilder<FollowUpTask> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("tasks", CrmSchema.Name);
        builder.HasKey(task => task.Id);
        builder.Property(task => task.Id).ValueGeneratedNever();

        builder.Property(task => task.Title).HasMaxLength(CrmLimits.MaxTaskTitleLength).IsRequired();
        builder.Property(task => task.RelatedType).HasConversion<string>().HasMaxLength(20).IsRequired();

        // Two people marking the same task done at once: the second is told it already is, rather
        // than both being told they did it.
        builder.Property(task => task.CompletedAt).IsConcurrencyToken();

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(task => task.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Customer>()
            .WithMany()
            .HasForeignKey(task => new { task.AgencyId, task.CustomerId })
            .HasPrincipalKey(customer => new { customer.AgencyId, customer.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Lead>()
            .WithMany()
            .HasForeignKey(task => new { task.AgencyId, task.LeadId })
            .HasPrincipalKey(lead => new { lead.AgencyId, lead.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(task => task.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // The task list's open tasks, soonest first.
        builder.HasIndex(task => new { task.AgencyId, task.DueAt })
            .HasFilter("completed_at IS NULL")
            .HasDatabaseName("ix_tasks_agency_id_due_at_open");

        // The reminder job's question, asked across every agency: what is falling due and not yet reminded?
        builder.HasIndex(task => task.DueAt)
            .HasFilter("completed_at IS NULL AND reminder_sent_at IS NULL")
            .HasDatabaseName("ix_tasks_due_at_awaiting_reminder");
    }
}

/// <summary><c>crm.communications</c>: each customer's timeline. Append-only for the application role.</summary>
public sealed class CommunicationConfiguration : IEntityTypeConfiguration<Communication>
{
    public void Configure(EntityTypeBuilder<Communication> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("communications", CrmSchema.Name);
        builder.HasKey(message => message.Id);
        builder.Property(message => message.Id).ValueGeneratedNever();

        builder.Property(message => message.Channel).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(message => message.Direction).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(message => message.RelatedType).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(message => message.Summary).HasMaxLength(CrmLimits.MaxSummaryLength).IsRequired();
        builder.Property(message => message.ByName).HasMaxLength(CrmLimits.MaxNameLength).IsRequired();

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(message => message.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Customer>()
            .WithMany()
            .HasForeignKey(message => new { message.AgencyId, message.CustomerId })
            .HasPrincipalKey(customer => new { customer.AgencyId, customer.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Lead>()
            .WithMany()
            .HasForeignKey(message => new { message.AgencyId, message.LeadId })
            .HasPrincipalKey(lead => new { lead.AgencyId, lead.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
