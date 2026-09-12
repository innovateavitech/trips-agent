using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TripsAgent.Domain.Catalog;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.Infrastructure.Persistence.Configurations.Catalog;

/// <summary>
/// <c>catalog.departures</c>: a dated run of a tour or package, sold by the seat (#57).
/// </summary>
/// <remarks>
/// <para>
/// The CHECK that makes overselling impossible — <c>capacity_reserved + capacity_confirmed &lt;=
/// capacity_total</c> — is in the migration, as hand-written SQL. EF Core cannot express it, and it
/// is the whole point of this table: two checkouts racing for the last seat are stopped by
/// PostgreSQL, not by hopeful application code.
/// </para>
/// <para>
/// The reference to the product is a <b>composite</b> foreign key, <c>(agency_id, product_id)</c>
/// against <c>(agency_id, id)</c>, for the reason <see cref="ProductConfiguration"/> gives: a plain
/// key would accept another agency's product id, because foreign-key checks run as the table owner
/// and skip row-level security.
/// </para>
/// </remarks>
public sealed class DepartureConfiguration : IEntityTypeConfiguration<Departure>
{
    public void Configure(EntityTypeBuilder<Departure> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("departures", CatalogSchema.Name);
        builder.HasKey(departure => departure.Id);
        builder.Property(departure => departure.Id).ValueGeneratedNever();

        // Stored by name, so a psql query reads "SoldOut" rather than "4".
        builder.Property(departure => departure.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(departure => departure.DepositType).HasConversion<string>().HasMaxLength(20).IsRequired();

        // Optimistic concurrency on the agent's edits only. Selling a seat does not touch it — see
        // Departure's own note — so a sale never invalidates an editor somebody has open.
        builder.Property(departure => departure.Version).IsConcurrencyToken().IsRequired();

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(departure => departure.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(departure => new { departure.AgencyId, departure.ProductId })
            .HasPrincipalKey(product => new { product.AgencyId, product.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_departures_products_product_id");

        // The tiers and the plan are part of the departure and are replaced when it is saved, so
        // they cascade. Holds, waitlist entries and manifest rows are not: they are records of
        // things that happened, and a departure is cancelled rather than deleted.
        builder.HasMany(departure => departure.PriceTiers)
            .WithOne()
            .HasForeignKey(tier => tier.DepartureId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(departure => departure.PriceTiers).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasOne(departure => departure.Installments)
            .WithOne()
            .HasForeignKey<InstallmentPlan>(plan => plan.DepartureId)
            .OnDelete(DeleteBehavior.Cascade);

        // The console's list, and the "from this date" filter it sends.
        builder.HasIndex(departure => new { departure.AgencyId, departure.ProductId, departure.DepartureDate })
            .HasDatabaseName("ix_departures_agency_id_product_id_departure_date");

        // The nightly status sweep (job 9) walks the ones that have not left yet.
        builder.HasIndex(departure => new { departure.Status, departure.DepartureDate })
            .HasDatabaseName("ix_departures_status_departure_date");
    }
}

/// <summary><c>catalog.departure_price_tiers</c>: the price ladder, by party size.</summary>
/// <remarks>
/// Replaced outright on every save rather than matched row by row, so there is deliberately no
/// unique index on <c>(departure_id, min_pax)</c>: EF Core can order the inserts of a replacement
/// before the deletes of what it replaces, and a unique index would see both at once. The ladder's
/// own integrity — contiguous from 1, the last one open-ended — is <see cref="DepartureRules"/>'s,
/// and the CHECKs in the migration bound each row.
/// </remarks>
public sealed class DeparturePriceTierConfiguration : IEntityTypeConfiguration<DeparturePriceTier>
{
    public void Configure(EntityTypeBuilder<DeparturePriceTier> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("departure_price_tiers", CatalogSchema.Name);
        builder.HasKey(tier => tier.Id);
        builder.Property(tier => tier.Id).ValueGeneratedNever();

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(tier => tier.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(tier => tier.DepartureId)
            .HasDatabaseName("ix_departure_price_tiers_departure_id");
    }
}

/// <summary><c>catalog.installment_plans</c>: how a departure's balance is paid.</summary>
public sealed class InstallmentPlanConfiguration : IEntityTypeConfiguration<InstallmentPlan>
{
    public void Configure(EntityTypeBuilder<InstallmentPlan> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("installment_plans", CatalogSchema.Name);
        builder.HasKey(plan => plan.Id);
        builder.Property(plan => plan.Id).ValueGeneratedNever();

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(plan => plan.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(plan => plan.Items)
            .WithOne()
            .HasForeignKey(item => item.InstallmentPlanId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(plan => plan.Items).UsePropertyAccessMode(PropertyAccessMode.Field);

        // One plan per departure. The one-to-one above says so in the model; this says so in the
        // database, where a stray second row would otherwise be possible.
        builder.HasIndex(plan => plan.DepartureId)
            .IsUnique()
            .HasDatabaseName("ix_installment_plans_departure_id");
    }
}

/// <summary><c>catalog.installment_schedule_items</c>: one payment of the balance, as an offset.</summary>
/// <remarks>Replaced outright with its plan, so it carries no unique index either — see the tiers.</remarks>
public sealed class InstallmentScheduleItemConfiguration : IEntityTypeConfiguration<InstallmentScheduleItem>
{
    public void Configure(EntityTypeBuilder<InstallmentScheduleItem> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("installment_schedule_items", CatalogSchema.Name);
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).ValueGeneratedNever();

        builder.Property(item => item.DueBasis).HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(item => item.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(item => item.InstallmentPlanId)
            .HasDatabaseName("ix_installment_schedule_items_installment_plan_id");
    }
}

/// <summary><c>catalog.departure_holds</c>: seats held for one cart during checkout.</summary>
public sealed class DepartureHoldConfiguration : IEntityTypeConfiguration<DepartureHold>
{
    public void Configure(EntityTypeBuilder<DepartureHold> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("departure_holds", CatalogSchema.Name);
        builder.HasKey(hold => hold.Id);
        builder.Property(hold => hold.Id).ValueGeneratedNever();

        builder.Property(hold => hold.Status).HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(hold => hold.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Departure>()
            .WithMany()
            .HasForeignKey(hold => new { hold.AgencyId, hold.DepartureId })
            .HasPrincipalKey(departure => new { departure.AgencyId, departure.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_departure_holds_departures_departure_id");

        // Composite again: a hold can only be for the agency's own cart.
        builder.HasOne<Cart>()
            .WithMany()
            .HasForeignKey(hold => new { hold.AgencyId, hold.CartId })
            .HasPrincipalKey(cart => new { cart.AgencyId, cart.Id })
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_departure_holds_carts_cart_id");

        // The seats one order line bought (build plan F5): how a paid line finds the hold to convert.
        // Partial, because a hold that is still only a cart's names no line.
        builder.HasIndex(hold => hold.OrderLineId)
            .HasFilter("order_line_id IS NOT NULL")
            .HasDatabaseName("ix_departure_holds_order_line_id");

        // What CartAndHoldExpiryJob (job 6) reads every minute: the held ones, oldest deadline first.
        builder.HasIndex(hold => new { hold.Status, hold.ExpiresAt })
            .HasDatabaseName("ix_departure_holds_status_expires_at");

        builder.HasIndex(hold => hold.DepartureId)
            .HasDatabaseName("ix_departure_holds_departure_id");
    }
}

/// <summary><c>catalog.departure_waitlist</c>: who is waiting for a seat, and in what order.</summary>
public sealed class DepartureWaitlistEntryConfiguration : IEntityTypeConfiguration<DepartureWaitlistEntry>
{
    /// <summary>Long enough for any name somebody types into a storefront form.</summary>
    public const int MaxNameLength = 200;

    /// <summary>The practical limit on an address, as the notification tables use.</summary>
    public const int MaxEmailLength = 320;

    public void Configure(EntityTypeBuilder<DepartureWaitlistEntry> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("departure_waitlist", CatalogSchema.Name);
        builder.HasKey(entry => entry.Id);
        builder.Property(entry => entry.Id).ValueGeneratedNever();

        builder.Property(entry => entry.Name).HasMaxLength(MaxNameLength).IsRequired();

        // citext, so the same person cannot join twice as Ada@x.com and ada@x.com.
        builder.Property(entry => entry.Email)
            .HasColumnType("citext")
            .HasMaxLength(MaxEmailLength)
            .IsRequired();

        builder.Property(entry => entry.Status).HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(entry => entry.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Departure>()
            .WithMany()
            .HasForeignKey(entry => new { entry.AgencyId, entry.DepartureId })
            .HasPrincipalKey(departure => new { departure.AgencyId, departure.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_departure_waitlist_departures_departure_id");

        // One place in the queue per person per departure. Joining again updates what is there.
        builder.HasIndex(entry => new { entry.DepartureId, entry.Email })
            .IsUnique()
            .HasDatabaseName("ix_departure_waitlist_departure_id_email");

        // The queue itself: the earliest waiting entry is offered the freed seat.
        builder.HasIndex(entry => new { entry.DepartureId, entry.Status, entry.JoinedAt })
            .HasDatabaseName("ix_departure_waitlist_departure_id_status_joined_at");

        // What WaitlistOfferExpiryJob (job 10) reads every five minutes.
        builder.HasIndex(entry => new { entry.Status, entry.ExpiresAt })
            .HasDatabaseName("ix_departure_waitlist_status_expires_at");
    }
}

/// <summary><c>catalog.pax_manifests</c>: who is on the departure, and which room they are in.</summary>
public sealed class PaxManifestEntryConfiguration : IEntityTypeConfiguration<PaxManifestEntry>
{
    /// <summary>"Room 12", "Twin share with Ada" — a label, not an essay.</summary>
    public const int MaxRoomLength = 100;

    public void Configure(EntityTypeBuilder<PaxManifestEntry> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("pax_manifests", CatalogSchema.Name);
        builder.HasKey(entry => entry.Id);
        builder.Property(entry => entry.Id).ValueGeneratedNever();

        builder.Property(entry => entry.RoomAssignment).HasMaxLength(MaxRoomLength);

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(entry => entry.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Departure>()
            .WithMany()
            .HasForeignKey(entry => new { entry.AgencyId, entry.DepartureId })
            .HasPrincipalKey(departure => new { departure.AgencyId, departure.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_pax_manifests_departures_departure_id");

        builder.HasOne<OrderLine>()
            .WithMany()
            .HasForeignKey(entry => new { entry.AgencyId, entry.OrderLineId })
            .HasPrincipalKey(line => new { line.AgencyId, line.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_pax_manifests_order_lines_order_line_id");

        builder.HasOne<OrderTraveller>()
            .WithMany()
            .HasForeignKey(entry => new { entry.AgencyId, entry.OrderTravellerId })
            .HasPrincipalKey(traveller => new { traveller.AgencyId, traveller.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_pax_manifests_order_travellers_order_traveller_id");

        // One seat per traveller per departure: the same person cannot appear on the manifest twice.
        builder.HasIndex(entry => new { entry.DepartureId, entry.OrderTravellerId })
            .IsUnique()
            .HasDatabaseName("ix_pax_manifests_departure_id_order_traveller_id");

        builder.HasIndex(entry => entry.OrderLineId)
            .HasDatabaseName("ix_pax_manifests_order_line_id");
    }
}

/// <summary>
/// <c>catalog.booking_payment_schedules</c>: what one booking on a departure pays, and when.
/// </summary>
/// <remarks>
/// A snapshot of the departure's terms taken on the day, so a later edit to those terms cannot move
/// a payment somebody has already been told about (CLAUDE.md rule 5). One per order line.
/// </remarks>
public sealed class BookingPaymentScheduleConfiguration : IEntityTypeConfiguration<BookingPaymentSchedule>
{
    /// <summary>Long enough for a full name as anybody writes it.</summary>
    public const int MaxContactNameLength = 200;

    /// <summary>The practical ceiling on an address; the same length identity uses.</summary>
    public const int MaxContactEmailLength = 320;

    public void Configure(EntityTypeBuilder<BookingPaymentSchedule> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("booking_payment_schedules", CatalogSchema.Name);
        builder.HasKey(schedule => schedule.Id);
        builder.Property(schedule => schedule.Id).ValueGeneratedNever();

        builder.Property(schedule => schedule.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(schedule => schedule.ContactName).HasMaxLength(MaxContactNameLength).IsRequired();
        builder.Property(schedule => schedule.ContactEmail).HasMaxLength(MaxContactEmailLength);

        // Not computed: a total read from the items would change if an item were ever cancelled,
        // and the point of a bill is that it does not.
        builder.Ignore(schedule => schedule.TotalMinor);
        builder.Ignore(schedule => schedule.OutstandingMinor);

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(schedule => schedule.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        // Composite, for the reason every other reference here is: a plain key would accept another
        // agency's departure, because foreign-key checks run as the table owner and skip RLS.
        builder.HasOne<Departure>()
            .WithMany()
            .HasForeignKey(schedule => new { schedule.AgencyId, schedule.DepartureId })
            .HasPrincipalKey(departure => new { departure.AgencyId, departure.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_booking_payment_schedules_departures_departure_id");

        builder.HasOne<OrderLine>()
            .WithMany()
            .HasForeignKey(schedule => new { schedule.AgencyId, schedule.OrderLineId })
            .HasPrincipalKey(line => new { line.AgencyId, line.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_booking_payment_schedules_order_lines_order_line_id");

        builder.HasMany(schedule => schedule.Items)
            .WithOne()
            .HasForeignKey(item => item.ScheduleId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(schedule => schedule.Items).UsePropertyAccessMode(PropertyAccessMode.Field);

        // One bill per line, said in the database as well as in the model.
        builder.HasIndex(schedule => schedule.OrderLineId)
            .IsUnique()
            .HasDatabaseName("ix_booking_payment_schedules_order_line_id");

        builder.HasIndex(schedule => new { schedule.AgencyId, schedule.DepartureId })
            .HasDatabaseName("ix_booking_payment_schedules_agency_id_departure_id");
    }
}

/// <summary><c>catalog.booking_installments</c>: one payment on a booking's schedule.</summary>
public sealed class BookingInstallmentConfiguration : IEntityTypeConfiguration<BookingInstallment>
{
    /// <summary>"Deposit", "Balance", "Payment 2" — a label, not a sentence.</summary>
    public const int MaxLabelLength = 60;

    public void Configure(EntityTypeBuilder<BookingInstallment> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("booking_installments", CatalogSchema.Name);
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).ValueGeneratedNever();

        builder.Property(item => item.Label).HasMaxLength(MaxLabelLength).IsRequired();
        builder.Property(item => item.State).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(item => item.LastReminderStage).HasConversion<string?>().HasMaxLength(20);

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(item => item.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(item => item.ScheduleId)
            .HasDatabaseName("ix_booking_installments_schedule_id");

        // What InstallmentReminderJob (job 11) reads every day: what is still owed, soonest first.
        builder.HasIndex(item => new { item.State, item.DueDate })
            .HasDatabaseName("ix_booking_installments_state_due_date");
    }
}
