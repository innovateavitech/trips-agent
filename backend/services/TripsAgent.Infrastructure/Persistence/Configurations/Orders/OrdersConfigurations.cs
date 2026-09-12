using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Pricing;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.Infrastructure.Persistence.Configurations.Orders;

/// <summary>Shared constants for the order tables.</summary>
public static class OrdersSchema
{
    /// <summary>The PostgreSQL schema holding orders, their lines and their travellers.</summary>
    public const string Name = "orders";
}

/// <summary>
/// <c>orders.orders</c>. The money-freezing trigger and the shape CHECKs are in the migration, as
/// hand-written SQL: EF Core can express neither.
/// </summary>
public sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("orders", OrdersSchema.Name);
        builder.HasKey(order => order.Id);
        builder.Property(order => order.Id).ValueGeneratedNever();

        builder.Property(order => order.OrderNumber).HasMaxLength(40).IsRequired();
        builder.Property(order => order.Currency).HasMaxLength(3).IsFixedLength().IsRequired();

        // Stored by name, not number, so a psql query reads "PendingPayment" rather than "1" — and so
        // reordering an enum can never silently change what a stored order means.
        builder.Property(order => order.Status).HasConversion<string>().HasMaxLength(30).IsRequired();

        // How and when it was paid, and the key the payment came with (#42). All three or none: the
        // migration's CHECK says so.
        builder.Property(order => order.PaidFrom).HasConversion<string>().HasMaxLength(20);
        builder.Property(order => order.PaymentIdempotencyKey).HasMaxLength(100);
        builder.Property(order => order.BuyerType).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(order => order.Channel).HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(order => order.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        // The lines are the aggregate's own; nothing outside loads them separately.
        builder.HasMany(order => order.Lines)
            .WithOne()
            .HasForeignKey(line => line.OrderId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(order => order.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(order => order.StatusHistory)
            .WithOne()
            .HasForeignKey(entry => entry.OrderId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(order => order.StatusHistory).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Gapless and unique per agency: two agencies may both have ORD-2026-000001.
        builder.HasIndex(order => new { order.AgencyId, order.OrderNumber })
            .IsUnique()
            .HasDatabaseName("ix_orders_agency_id_order_number");

        builder.HasIndex(order => new { order.AgencyId, order.Status })
            .HasDatabaseName("ix_orders_agency_id_status");

        // The idempotency key at the API's edge: the same key twice is one booking, never a second charge.
        builder.HasIndex(order => new { order.AgencyId, order.PaymentIdempotencyKey })
            .IsUnique()
            .HasFilter("payment_idempotency_key IS NOT NULL")
            .HasDatabaseName("ix_orders_agency_id_payment_idempotency_key");
    }
}

/// <summary><c>orders.order_lines</c>. Every money column here is frozen once placed_at is set.</summary>
public sealed class OrderLineConfiguration : IEntityTypeConfiguration<OrderLine>
{
    public void Configure(EntityTypeBuilder<OrderLine> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("order_lines", OrdersSchema.Name);
        builder.HasKey(line => line.Id);
        builder.Property(line => line.Id).ValueGeneratedNever();

        builder.Property(line => line.ItemType).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(line => line.FulfilmentStatus).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(line => line.ResolutionStatus).HasConversion<string>().HasMaxLength(30);
        builder.Property(line => line.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(line => line.TitleSnapshot).HasMaxLength(300).IsRequired();
        builder.Property(line => line.FailureReason).HasMaxLength(500);

        // Passengers by type. jsonb so a report can read into it without parsing text.
        builder.Property(line => line.PaxBreakdown).HasColumnType("jsonb").IsRequired();

        builder.Ignore(line => line.AgentMarginMinor);

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(line => line.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        // The quote every figure was copied from. Restrict: a quote that priced a sale is evidence.
        builder.HasOne<PriceQuote>()
            .WithMany()
            .HasForeignKey(line => line.PriceQuoteId)
            .OnDelete(DeleteBehavior.Restrict);

        // (agency_id, fulfilment_status) IS the agent's resolution queue — see issue #44.
        builder.HasIndex(line => new { line.AgencyId, line.FulfilmentStatus })
            .HasDatabaseName("ix_order_lines_agency_id_fulfilment_status");

        builder.HasIndex(line => line.PriceQuoteId).HasDatabaseName("ix_order_lines_price_quote_id");
    }
}

/// <summary><c>orders.order_travellers</c>. Passport numbers are stored encrypted, never in clear.</summary>
public sealed class OrderTravellerConfiguration : IEntityTypeConfiguration<OrderTraveller>
{
    public void Configure(EntityTypeBuilder<OrderTraveller> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("order_travellers", OrdersSchema.Name);
        builder.HasKey(traveller => traveller.Id);
        builder.Property(traveller => traveller.Id).ValueGeneratedNever();

        builder.Property(traveller => traveller.TravellerType).HasConversion<string>().HasMaxLength(10).IsRequired();
        builder.Property(traveller => traveller.FirstName).HasMaxLength(100).IsRequired();
        builder.Property(traveller => traveller.LastName).HasMaxLength(100).IsRequired();
        builder.Property(traveller => traveller.Nationality).HasMaxLength(2).IsFixedLength();

        // Ciphertext, not text: AES-GCM output from ISecretProtector. The column name ends in
        // _encrypted so nobody reading the schema mistakes it for something they can search on.
        builder.Property(traveller => traveller.PassportNumberEncrypted)
            .HasColumnName("passport_number_encrypted")
            .HasColumnType("bytea");

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(traveller => traveller.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<OrderLine>()
            .WithMany()
            .HasForeignKey(traveller => traveller.OrderLineId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(traveller => traveller.OrderLineId).HasDatabaseName("ix_order_travellers_order_line_id");

        // Leads with agency_id, like every tenant-scoped index. It replaces the plain agency_id index
        // EF kept until the alternate key (agency_id, id) was added for the departures manifest's
        // composite foreign key: a unique constraint is an index in PostgreSQL, but not one the
        // model exposes, and TenantFilterCoverageTests reads the model.
        builder.HasIndex(traveller => new { traveller.AgencyId, traveller.OrderLineId })
            .HasDatabaseName("ix_order_travellers_agency_id_order_line_id");
    }
}

/// <summary>
/// <c>orders.order_status_history</c>. Append-only: the migration grants SELECT and INSERT and
/// withholds UPDATE and DELETE, so nothing the application can do rewrites the trail.
/// </summary>
public sealed class OrderStatusHistoryConfiguration : IEntityTypeConfiguration<OrderStatusHistory>
{
    public void Configure(EntityTypeBuilder<OrderStatusHistory> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("order_status_history", OrdersSchema.Name);
        builder.HasKey(entry => entry.Id);
        builder.Property(entry => entry.Id).ValueGeneratedNever();

        builder.Property(entry => entry.FromStatus).HasConversion<string>().HasMaxLength(30);
        builder.Property(entry => entry.ToStatus).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(entry => entry.Reason).HasMaxLength(500);

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(entry => entry.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        // The trail for one order, oldest first — how it is always read.
        builder.HasIndex(entry => new { entry.OrderId, entry.ChangedAt })
            .HasDatabaseName("ix_order_status_history_order_id_changed_at");
    }
}

/// <summary>
/// <c>orders.carts</c>. Unlike an order, a cart carries no frozen money and no immutability
/// trigger — see the remarks on <see cref="Cart"/> for why that is deliberate.
/// </summary>
public sealed class CartConfiguration : IEntityTypeConfiguration<Cart>
{
    public void Configure(EntityTypeBuilder<Cart> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("carts", OrdersSchema.Name);
        builder.HasKey(cart => cart.Id);
        builder.Property(cart => cart.Id).ValueGeneratedNever();

        builder.Property(cart => cart.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(cart => cart.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(cart => cart.SessionToken).HasMaxLength(128);

        builder.Ignore(cart => cart.IndicativeTotalMinor);

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(cart => cart.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(cart => cart.Items)
            .WithOne()
            .HasForeignKey(item => item.CartId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(cart => cart.Items).UsePropertyAccessMode(PropertyAccessMode.Field);

        // How a guest's browser finds its cart again. Partial: a signed-in cart has no token, and
        // thousands of NULLs in a unique index would be thousands of rows that never collide.
        builder.HasIndex(cart => new { cart.AgencyId, cart.SessionToken })
            .IsUnique()
            .HasFilter("session_token IS NOT NULL")
            .HasDatabaseName("ix_carts_agency_id_session_token");

        // The sweeper's query: which carts have timed out.
        builder.HasIndex(cart => new { cart.Status, cart.ExpiresAt })
            .HasDatabaseName("ix_carts_status_expires_at");
    }
}

/// <summary><c>orders.cart_items</c>. Deleted with their cart — a cart is not a record of anything.</summary>
public sealed class CartItemConfiguration : IEntityTypeConfiguration<CartItem>
{
    public void Configure(EntityTypeBuilder<CartItem> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("cart_items", OrdersSchema.Name);
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).ValueGeneratedNever();

        builder.Property(item => item.ItemType).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(item => item.TitleSnapshot).HasMaxLength(200).IsRequired();
        builder.Property(item => item.PaxBreakdown).HasColumnType("jsonb").IsRequired();
        builder.Property(item => item.Currency).HasMaxLength(3).IsFixedLength().IsRequired();

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(item => item.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<PriceQuote>()
            .WithMany()
            .HasForeignKey(item => item.PriceQuoteId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(item => item.CartId).HasDatabaseName("ix_cart_items_cart_id");
    }
}
