using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TripsAgent.Domain.Billing;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.Infrastructure.Persistence.Configurations.Billing;

/// <summary>Shared constants for the subscription and billing tables.</summary>
public static class BillingSchema
{
    /// <summary>The PostgreSQL schema holding tiers, entitlements, subscriptions and their invoices.</summary>
    public const string Name = "billing";
}

/// <summary>
/// <c>billing.entitlements</c> — the catalogue of things a tier can grant.
/// </summary>
/// <remarks>
/// Platform-owned reference data, seeded from <see cref="EntitlementCatalog"/>. No
/// <c>agency_id</c>, so no tenant filter and no row-level security policy: there is nothing here
/// belonging to any agency, and it is only ever written behind <c>subscription.manage</c>.
/// </remarks>
public sealed class EntitlementConfiguration : IEntityTypeConfiguration<Entitlement>
{
    public void Configure(EntityTypeBuilder<Entitlement> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("entitlements", BillingSchema.Name);
        builder.HasKey(entitlement => entitlement.Id);
        builder.Property(entitlement => entitlement.Id).ValueGeneratedNever();

        builder.Property(entitlement => entitlement.Code).HasMaxLength(60).IsRequired();
        builder.Property(entitlement => entitlement.Name).HasMaxLength(120).IsRequired();
        builder.Property(entitlement => entitlement.Description).HasMaxLength(500).IsRequired();

        // By name, so psql reads "Limit" rather than "2" and reordering the enum cannot silently
        // turn every ceiling in the table into a percentage.
        builder.Property(entitlement => entitlement.ValueType).HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.HasIndex(entitlement => entitlement.Code)
            .IsUnique()
            .HasDatabaseName("ix_entitlements_code");
    }
}

/// <summary><c>billing.subscription_tiers</c> — a plan. Platform-owned; archived, never deleted.</summary>
public sealed class SubscriptionTierConfiguration : IEntityTypeConfiguration<SubscriptionTier>
{
    public void Configure(EntityTypeBuilder<SubscriptionTier> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("subscription_tiers", BillingSchema.Name);
        builder.HasKey(tier => tier.Id);
        builder.Property(tier => tier.Id).ValueGeneratedNever();

        builder.Property(tier => tier.Code).HasMaxLength(60).IsRequired();
        builder.Property(tier => tier.Name).HasMaxLength(120).IsRequired();
        builder.Property(tier => tier.CustomerDescription).HasMaxLength(500);
        builder.Property(tier => tier.Status).HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.HasMany(tier => tier.Prices)
            .WithOne()
            .HasForeignKey(price => price.TierId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(tier => tier.Prices).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Cascade, unlike the prices: a grant is a row in a join table with no meaning of its own
        // once its tier is gone, whereas a price is what somebody was charged.
        builder.HasMany(tier => tier.Entitlements)
            .WithOne()
            .HasForeignKey(grant => grant.TierId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(tier => tier.Entitlements).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(tier => tier.Code)
            .IsUnique()
            .HasDatabaseName("ix_subscription_tiers_code");

        // At most one fallback plan. A second one would make "where does a failed payment land?"
        // ambiguous, and the answer to that question decides whether an agency keeps trading.
        builder.HasIndex(tier => tier.IsFallback)
            .IsUnique()
            .HasFilter("is_fallback")
            .HasDatabaseName("ix_subscription_tiers_fallback");

        builder.HasIndex(tier => new { tier.Status, tier.SortOrder })
            .HasDatabaseName("ix_subscription_tiers_status_sort_order");
    }
}

/// <summary><c>billing.tier_prices</c> — what a tier cost, over one stretch of time. Rows are closed, never edited.</summary>
public sealed class TierPriceConfiguration : IEntityTypeConfiguration<TierPrice>
{
    public void Configure(EntityTypeBuilder<TierPrice> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("tier_prices", BillingSchema.Name);
        builder.HasKey(price => price.Id);
        builder.Property(price => price.Id).ValueGeneratedNever();

        builder.Property(price => price.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(price => price.Interval).HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.HasIndex(price => new { price.TierId, price.Currency, price.Interval, price.EffectiveTo })
            .HasDatabaseName("ix_tier_prices_tier_id_currency_interval_effective_to");
    }
}

/// <summary><c>billing.tier_entitlements</c> — what one tier grants.</summary>
public sealed class TierEntitlementConfiguration : IEntityTypeConfiguration<TierEntitlement>
{
    public void Configure(EntityTypeBuilder<TierEntitlement> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("tier_entitlements", BillingSchema.Name);
        builder.HasKey(grant => grant.Id);
        builder.Property(grant => grant.Id).ValueGeneratedNever();

        builder.Property(grant => grant.ValueType).HasConversion<string>().HasMaxLength(20).IsRequired();

        // jsonb rather than text, because the plan's schema says jsonb and because a report can
        // then read into the value without parsing it out of a string first.
        builder.Property(grant => grant.Value).HasColumnType("jsonb").IsRequired();

        builder.HasOne<Entitlement>()
            .WithMany()
            .HasForeignKey(grant => grant.EntitlementId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(grant => new { grant.TierId, grant.EntitlementId })
            .IsUnique()
            .HasDatabaseName("ix_tier_entitlements_tier_id_entitlement_id");
    }
}

/// <summary>
/// <c>billing.subscriptions</c> — one agency's plan. Tenant-scoped, so the query filter and the
/// row-level security policy both apply.
/// </summary>
public sealed class SubscriptionConfiguration : IEntityTypeConfiguration<Subscription>
{
    public void Configure(EntityTypeBuilder<Subscription> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("subscriptions", BillingSchema.Name);
        builder.HasKey(subscription => subscription.Id);
        builder.Property(subscription => subscription.Id).ValueGeneratedNever();

        builder.Property(subscription => subscription.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(subscription => subscription.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(subscription => subscription.ExternalRef).HasMaxLength(120);
        builder.Property(subscription => subscription.StatusReason).HasMaxLength(500);

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(subscription => subscription.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<SubscriptionTier>()
            .WithMany()
            .HasForeignKey(subscription => subscription.TierId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<TierPrice>()
            .WithMany()
            .HasForeignKey(subscription => subscription.TierPriceId)
            .OnDelete(DeleteBehavior.Restrict);

        // One live subscription per agency. A partial unique index rather than a check in a service,
        // because two concurrent requests both passing a service check is exactly how an agency ends
        // up billed twice in one month.
        builder.HasIndex(subscription => subscription.AgencyId)
            .IsUnique()
            .HasFilter("status IN ('Trialing', 'Active', 'PastDue')")
            .HasDatabaseName("ix_subscriptions_agency_id_live");

        // The renewal job's query: everything whose period has run out.
        builder.HasIndex(subscription => new { subscription.Status, subscription.CurrentPeriodEnd })
            .HasDatabaseName("ix_subscriptions_status_current_period_end");

        builder.HasIndex(subscription => subscription.TierId)
            .HasDatabaseName("ix_subscriptions_tier_id");
    }
}

/// <summary><c>billing.subscription_invoices</c> — what Trips charged one agency.</summary>
public sealed class SubscriptionInvoiceConfiguration : IEntityTypeConfiguration<SubscriptionInvoice>
{
    public void Configure(EntityTypeBuilder<SubscriptionInvoice> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("subscription_invoices", BillingSchema.Name);
        builder.HasKey(invoice => invoice.Id);
        builder.Property(invoice => invoice.Id).ValueGeneratedNever();

        builder.Property(invoice => invoice.InvoiceNumber).HasMaxLength(40).IsRequired();
        builder.Property(invoice => invoice.ReceiptNumber).HasMaxLength(40);
        builder.Property(invoice => invoice.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(invoice => invoice.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(invoice => invoice.StatusReason).HasMaxLength(500);

        builder.Ignore(invoice => invoice.AddsUp);
        builder.Ignore(invoice => invoice.IsPayable);

        builder.HasMany(invoice => invoice.Lines)
            .WithOne()
            .HasForeignKey(line => line.InvoiceId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(invoice => invoice.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(invoice => invoice.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Subscription>()
            .WithMany()
            .HasForeignKey(invoice => invoice.SubscriptionId)
            .OnDelete(DeleteBehavior.Restrict);

        // Ours, not an agency's, so it is unique across the whole platform rather than per tenant.
        builder.HasIndex(invoice => invoice.InvoiceNumber)
            .IsUnique()
            .HasDatabaseName("ix_subscription_invoices_invoice_number");

        builder.HasIndex(invoice => invoice.ReceiptNumber)
            .IsUnique()
            .HasFilter("receipt_number IS NOT NULL")
            .HasDatabaseName("ix_subscription_invoices_receipt_number");

        builder.HasIndex(invoice => new { invoice.AgencyId, invoice.IssuedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_subscription_invoices_agency_id_issued_at");

        // The dunning job's query: everything still owed, oldest first.
        builder.HasIndex(invoice => new { invoice.Status, invoice.DueAt })
            .HasDatabaseName("ix_subscription_invoices_status_due_at");
    }
}

/// <summary><c>billing.subscription_invoice_lines</c> — the lines whose sum is the invoice total.</summary>
public sealed class SubscriptionInvoiceLineConfiguration : IEntityTypeConfiguration<SubscriptionInvoiceLine>
{
    public void Configure(EntityTypeBuilder<SubscriptionInvoiceLine> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("subscription_invoice_lines", BillingSchema.Name);
        builder.HasKey(line => line.Id);
        builder.Property(line => line.Id).ValueGeneratedNever();

        builder.Property(line => line.Description).HasMaxLength(300).IsRequired();

        // Leading with agency_id because every read of a line carries the tenant filter's
        // `WHERE agency_id = …`, and an index that does not lead with it cannot serve that.
        builder.HasIndex(line => new { line.AgencyId, line.InvoiceId })
            .HasDatabaseName("ix_subscription_invoice_lines_agency_id_invoice_id");
    }
}

/// <summary><c>billing.subscription_charge_attempts</c> — every try at taking the money. Append-only.</summary>
public sealed class SubscriptionChargeAttemptConfiguration : IEntityTypeConfiguration<SubscriptionChargeAttempt>
{
    public void Configure(EntityTypeBuilder<SubscriptionChargeAttempt> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("subscription_charge_attempts", BillingSchema.Name);
        builder.HasKey(attempt => attempt.Id);
        builder.Property(attempt => attempt.Id).ValueGeneratedNever();

        builder.Property(attempt => attempt.Reference).HasMaxLength(100).IsRequired();
        builder.Property(attempt => attempt.Outcome).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(attempt => attempt.FailureReason)
            .HasMaxLength(SubscriptionChargeAttempt.MaxFailureReasonLength);

        builder.HasOne<SubscriptionInvoice>()
            .WithMany()
            .HasForeignKey(attempt => attempt.InvoiceId)
            .OnDelete(DeleteBehavior.Restrict);

        // One row per attempt number per invoice. The unique index is what makes the billing job
        // safe to replay: a second run of the same attempt is rejected by the database rather than
        // charging the card again.
        builder.HasIndex(attempt => new { attempt.InvoiceId, attempt.AttemptNumber })
            .IsUnique()
            .HasDatabaseName("ix_subscription_charge_attempts_invoice_id_attempt_number");

        builder.HasIndex(attempt => attempt.Reference)
            .IsUnique()
            .HasDatabaseName("ix_subscription_charge_attempts_reference");

        // The agency's own "why was I charged?" view, newest first.
        builder.HasIndex(attempt => new { attempt.AgencyId, attempt.AttemptedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_subscription_charge_attempts_agency_id_attempted_at");
    }
}

/// <summary><c>billing.payment_authorizations</c> — reusable gateway tokens. No card data, ever.</summary>
public sealed class PaymentAuthorizationConfiguration : IEntityTypeConfiguration<PaymentAuthorization>
{
    public void Configure(EntityTypeBuilder<PaymentAuthorization> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("payment_authorizations", BillingSchema.Name);
        builder.HasKey(authorization => authorization.Id);
        builder.Property(authorization => authorization.Id).ValueGeneratedNever();

        builder.Property(authorization => authorization.Gateway).HasMaxLength(30).IsRequired();
        builder.Property(authorization => authorization.AuthorizationCode).HasMaxLength(200).IsRequired();
        builder.Property(authorization => authorization.CardBrand).HasMaxLength(30);
        builder.Property(authorization => authorization.Last4).HasMaxLength(4);
        builder.Property(authorization => authorization.ExpiryMonth).HasMaxLength(2);
        builder.Property(authorization => authorization.ExpiryYear).HasMaxLength(4);
        builder.Property(authorization => authorization.Bank).HasMaxLength(120);

        builder.Ignore(authorization => authorization.Display);
        builder.Ignore(authorization => authorization.IsChargeable);

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(authorization => authorization.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        // One default card per agency. Two would make "which card do we charge?" a coin toss.
        builder.HasIndex(authorization => authorization.AgencyId)
            .IsUnique()
            .HasFilter("is_default AND revoked_at IS NULL")
            .HasDatabaseName("ix_payment_authorizations_agency_id_default");

        builder.HasIndex(authorization => new { authorization.AgencyId, authorization.AuthorizationCode })
            .IsUnique()
            .HasDatabaseName("ix_payment_authorizations_agency_id_authorization_code");
    }
}

/// <summary><c>billing.subscription_migrations</c> — a tier change that has been agreed but not applied.</summary>
public sealed class SubscriptionMigrationConfiguration : IEntityTypeConfiguration<SubscriptionMigration>
{
    public void Configure(EntityTypeBuilder<SubscriptionMigration> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("subscription_migrations", BillingSchema.Name);
        builder.HasKey(migration => migration.Id);
        builder.Property(migration => migration.Id).ValueGeneratedNever();

        builder.Property(migration => migration.ChangeReason).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(migration => migration.Reason).HasMaxLength(500).IsRequired();

        builder.Ignore(migration => migration.IsPending);

        builder.HasOne<Subscription>()
            .WithMany()
            .HasForeignKey(migration => migration.SubscriptionId)
            .OnDelete(DeleteBehavior.Restrict);

        // The job's query: everything still to apply, soonest first.
        builder.HasIndex(migration => new { migration.ScheduledFor, migration.AppliedAt })
            .HasDatabaseName("ix_subscription_migrations_scheduled_for_applied_at");

        builder.HasIndex(migration => migration.AgencyId)
            .HasDatabaseName("ix_subscription_migrations_agency_id");
    }
}

/// <summary><c>billing.tier_change_log</c> — what an admin changed about a tier, and who was told.</summary>
public sealed class TierChangeLogEntryConfiguration : IEntityTypeConfiguration<TierChangeLogEntry>
{
    public void Configure(EntityTypeBuilder<TierChangeLogEntry> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("tier_change_log", BillingSchema.Name);
        builder.HasKey(entry => entry.Id);
        builder.Property(entry => entry.Id).ValueGeneratedNever();

        builder.Property(entry => entry.Action).HasMaxLength(60).IsRequired();
        builder.Property(entry => entry.Reason).HasMaxLength(1000).IsRequired();
        builder.Property(entry => entry.MigrationPolicy).HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.Property(entry => entry.Before).HasColumnType("jsonb");
        builder.Property(entry => entry.After).HasColumnType("jsonb");

        // No foreign key to subscription_tiers, deliberately. The log outlives what it is about:
        // the one tier that can be deleted is an unpublished draft, and the record of somebody
        // creating and then deleting it is exactly the record worth keeping. Same reasoning as
        // platform.admin_alerts, which records an agency id with no FK to the agency.
        builder.Property(entry => entry.TierId);

        builder.HasIndex(entry => new { entry.TierId, entry.OccurredAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_tier_change_log_tier_id_occurred_at");
    }
}
