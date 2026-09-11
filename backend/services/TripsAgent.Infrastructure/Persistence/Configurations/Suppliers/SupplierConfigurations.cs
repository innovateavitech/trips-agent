using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TripsAgent.Domain.Suppliers;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.Infrastructure.Persistence.Configurations.Suppliers;

/// <summary>
/// The <c>supplier</c> schema: the Trips Africa abstraction (plan §2.7, issue #32).
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here names a supplier.</b> Supplier identity is a row in <c>suppliers</c>; product,
/// status and operation columns are our own enums stored as text, with no CHECK constraint tying them
/// to Trips Africa's vocabulary; and the supplier's own words (trip type, trip mode, status code) are
/// kept verbatim in plain columns. The offer's identity tuple is all-nullable beside a generic
/// <c>offer_ref</c>, so an aggregator that identifies offers by one token fits too. That is how a
/// second aggregator arrives without a migration.
/// </para>
/// <para>
/// Every table owned by an agency leads an index with <c>agency_id</c> and is policed by row-level
/// security in the migration. <c>supplier_api_calls</c> is range-partitioned by month, which EF cannot
/// express — the migration creates it in SQL, and this configuration describes the same columns.
/// </para>
/// </remarks>
public static class SupplierSchema
{
    /// <summary>The PostgreSQL schema.</summary>
    public const string Name = "supplier";

    /// <summary>The partitioned call log. Named once because the migration's SQL and the maintenance job both need it.</summary>
    public const string ApiCallsTable = "supplier_api_calls";

    /// <summary>A SHA-512 digest in hex, which is what Trips Africa's price hash is.</summary>
    public const int HashLength = 128;
}

public sealed class SupplierConfiguration : IEntityTypeConfiguration<Supplier>
{
    public void Configure(EntityTypeBuilder<Supplier> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("suppliers", SupplierSchema.Name);
        builder.HasKey(supplier => supplier.Id);
        builder.Property(supplier => supplier.Id).ValueGeneratedNever();

        builder.Property(supplier => supplier.Code).HasMaxLength(Supplier.MaxCodeLength).IsRequired();
        builder.Property(supplier => supplier.Name).HasMaxLength(120).IsRequired();
        builder.Property(supplier => supplier.Kind).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(supplier => supplier.BaseUrl).HasMaxLength(500).IsRequired();
        builder.Property(supplier => supplier.Config).HasColumnType("jsonb").IsRequired();

        // Adapters are found by code, so two rows with one code would make it ambiguous which
        // supplier a booking went to.
        builder.HasIndex(supplier => supplier.Code).IsUnique().HasDatabaseName("ix_suppliers_code");
    }
}

public sealed class SupplierCredentialConfiguration : IEntityTypeConfiguration<SupplierCredential>
{
    public void Configure(EntityTypeBuilder<SupplierCredential> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("supplier_credentials", SupplierSchema.Name);
        builder.HasKey(credential => credential.Id);
        builder.Property(credential => credential.Id).ValueGeneratedNever();

        builder.Property(credential => credential.Environment).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(credential => credential.MerchantCode).HasMaxLength(100).IsRequired();

        // Ciphertext only — see AesGcmSecretProtector. There is no plaintext column to fall back to.
        builder.Property(credential => credential.MerchantKeyEncrypted).HasColumnType("bytea").IsRequired();
        builder.Property(credential => credential.BearerTokenEncrypted).HasColumnType("bytea");

        builder.Ignore(credential => credential.IsPlatformLevel);

        builder.HasOne<Supplier>()
            .WithMany()
            .HasForeignKey(credential => credential.SupplierId)
            .OnDelete(DeleteBehavior.Restrict);

        // Nullable: null is the platform's own merchant account (open question 1).
        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(credential => credential.AgencyId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        // One credential per owner, supplier and environment. NULLS NOT DISTINCT so the platform —
        // whose agency is null — is also limited to one: by default PostgreSQL treats every null as
        // different, and two platform credentials would make "which key signs this hash" a coin toss.
        builder.HasIndex(credential => new { credential.AgencyId, credential.SupplierId, credential.Environment })
            .IsUnique()
            .AreNullsDistinct(false)
            .HasDatabaseName("ix_supplier_credentials_owner_supplier_environment");
    }
}

public sealed class SearchRequestConfiguration : IEntityTypeConfiguration<SearchRequest>
{
    public void Configure(EntityTypeBuilder<SearchRequest> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("search_requests", SupplierSchema.Name);
        builder.HasKey(search => search.Id);
        builder.Property(search => search.Id).ValueGeneratedNever();

        builder.Property(search => search.ProductType).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(search => search.CriteriaHash).HasMaxLength(64).IsFixedLength().IsRequired();
        builder.Property(search => search.Criteria).HasColumnType("jsonb").IsRequired();
        builder.Property(search => search.TripType).HasMaxLength(30);
        builder.Property(search => search.ErrorCode).HasMaxLength(100);

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(search => search.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        // The conversion report: an agency's searches over time.
        builder.HasIndex(search => new { search.AgencyId, search.RequestedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_search_requests_agency_id_requested_at");

        builder.HasIndex(search => new { search.AgencyId, search.CriteriaHash })
            .HasDatabaseName("ix_search_requests_agency_id_criteria_hash");
    }
}

public sealed class SearchSessionConfiguration : IEntityTypeConfiguration<SearchSession>
{
    public void Configure(EntityTypeBuilder<SearchSession> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("search_sessions", SupplierSchema.Name);
        builder.HasKey(session => session.Id);
        builder.Property(session => session.Id).ValueGeneratedNever();

        builder.Property(session => session.SupplierSessionId).HasMaxLength(200).IsRequired();
        builder.Property(session => session.GdsSessionId).HasMaxLength(200);

        builder.HasOne<SearchRequest>()
            .WithMany()
            .HasForeignKey(session => session.SearchRequestId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Supplier>()
            .WithMany()
            .HasForeignKey(session => session.SupplierId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(session => new { session.AgencyId, session.SearchRequestId })
            .HasDatabaseName("ix_search_sessions_agency_id_search_request_id");

        builder.HasIndex(session => new { session.SupplierId, session.SupplierSessionId })
            .HasDatabaseName("ix_search_sessions_supplier_id_supplier_session_id");
    }
}

public sealed class SupplierOfferConfiguration : IEntityTypeConfiguration<SupplierOffer>
{
    public void Configure(EntityTypeBuilder<SupplierOffer> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("supplier_offers", SupplierSchema.Name);
        builder.HasKey(offer => offer.Id);
        builder.Property(offer => offer.Id).ValueGeneratedNever();

        builder.Property(offer => offer.ProductType).HasConversion<string>().HasMaxLength(20).IsRequired();

        // The supplier's handle and identity tuple, opaque and round-tripped exactly. Generous
        // lengths: these are someone else's identifiers, and truncating one breaks price confirmation.
        builder.Property(offer => offer.OfferRef).HasMaxLength(500).IsRequired();
        builder.Property(offer => offer.AgentIdExt).HasMaxLength(200);
        builder.Property(offer => offer.GdsIdExt).HasMaxLength(200);
        builder.Property(offer => offer.CombinationId).HasMaxLength(200);
        builder.Property(offer => offer.RecommendationId).HasMaxLength(200);

        builder.Property(offer => offer.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(offer => offer.RawPayload).HasColumnType("jsonb").IsRequired();

        builder.Ignore(offer => offer.Reference);

        builder.HasOne<SearchSession>()
            .WithMany()
            .HasForeignKey(offer => offer.SearchSessionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Supplier>()
            .WithMany()
            .HasForeignKey(offer => offer.SupplierId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(offer => new { offer.AgencyId, offer.SearchSessionId })
            .HasDatabaseName("ix_supplier_offers_agency_id_search_session_id");
    }
}

public sealed class FlightSegmentConfiguration : IEntityTypeConfiguration<FlightSegment>
{
    public void Configure(EntityTypeBuilder<FlightSegment> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("flight_segments", SupplierSchema.Name);
        builder.HasKey(segment => segment.Id);
        builder.Property(segment => segment.Id).ValueGeneratedNever();

        builder.Property(segment => segment.MarketingCarrier).HasMaxLength(3).IsRequired();
        builder.Property(segment => segment.OperatingCarrier).HasMaxLength(3);
        builder.Property(segment => segment.FlightNumber).HasMaxLength(10).IsRequired();
        builder.Property(segment => segment.OriginIata).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(segment => segment.DestinationIata).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(segment => segment.Cabin).HasMaxLength(30);
        builder.Property(segment => segment.BaggageAllowance).HasMaxLength(100);
        builder.Property(segment => segment.FareBasis).HasMaxLength(30);

        builder.HasOne<SupplierOffer>()
            .WithMany()
            .HasForeignKey(segment => segment.SupplierOfferId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(segment => new { segment.AgencyId, segment.SupplierOfferId })
            .HasDatabaseName("ix_flight_segments_agency_id_supplier_offer_id");

        // One flight per position in the journey. Two at the same position is an itinerary nobody can fly.
        builder.HasIndex(segment => new { segment.SupplierOfferId, segment.LegIndex, segment.SegmentIndex })
            .IsUnique()
            .HasDatabaseName("ix_flight_segments_offer_leg_segment");
    }
}

public sealed class BusSegmentConfiguration : IEntityTypeConfiguration<BusSegment>
{
    public void Configure(EntityTypeBuilder<BusSegment> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("bus_segments", SupplierSchema.Name);
        builder.HasKey(segment => segment.Id);
        builder.Property(segment => segment.Id).ValueGeneratedNever();

        builder.Property(segment => segment.OperatorName).HasMaxLength(200).IsRequired();
        builder.Property(segment => segment.DepartureTerminalId).HasMaxLength(100).IsRequired();
        builder.Property(segment => segment.ArrivalTerminalId).HasMaxLength(100).IsRequired();
        builder.Property(segment => segment.SeatNumbers).HasColumnType("jsonb");
        builder.Property(segment => segment.ReservationIdExt).HasMaxLength(200);

        builder.HasOne<SupplierOffer>()
            .WithMany()
            .HasForeignKey(segment => segment.SupplierOfferId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(segment => new { segment.AgencyId, segment.SupplierOfferId })
            .HasDatabaseName("ix_bus_segments_agency_id_supplier_offer_id");
    }
}

public sealed class SupplierFareRuleConfiguration : IEntityTypeConfiguration<SupplierFareRule>
{
    public void Configure(EntityTypeBuilder<SupplierFareRule> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("supplier_fare_rules", SupplierSchema.Name);
        builder.HasKey(rule => rule.Id);
        builder.Property(rule => rule.Id).ValueGeneratedNever();

        builder.Property(rule => rule.RulesHtml).HasColumnType("text");
        builder.Property(rule => rule.Penalties).HasColumnType("jsonb");

        // Restrict, not cascade: once a booking was sold under these rules they are part of the sale,
        // and clearing out old search results must not delete them.
        builder.HasOne<SupplierOffer>()
            .WithMany()
            .HasForeignKey(rule => rule.SupplierOfferId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<SupplierBooking>()
            .WithMany()
            .HasForeignKey(rule => rule.SupplierBookingId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(rule => new { rule.AgencyId, rule.SupplierOfferId })
            .HasDatabaseName("ix_supplier_fare_rules_agency_id_supplier_offer_id");

        builder.HasIndex(rule => rule.SupplierBookingId)
            .HasDatabaseName("ix_supplier_fare_rules_supplier_booking_id");
    }
}

public sealed class SupplierBookingConfiguration : IEntityTypeConfiguration<SupplierBooking>
{
    public void Configure(EntityTypeBuilder<SupplierBooking> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("supplier_bookings", SupplierSchema.Name);
        builder.HasKey(booking => booking.Id);
        builder.Property(booking => booking.Id).ValueGeneratedNever();

        builder.Property(booking => booking.ProductType).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(booking => booking.Status).HasConversion<string>().HasMaxLength(30).IsRequired();

        // The supplier's own words, kept verbatim so they can be sent back exactly.
        builder.Property(booking => booking.TripType).HasMaxLength(30);
        builder.Property(booking => booking.TripMode).HasMaxLength(30);
        builder.Property(booking => booking.SupplierSessionId).HasMaxLength(200).IsRequired();
        builder.Property(booking => booking.ConfirmationCode).HasMaxLength(100);
        builder.Property(booking => booking.Pnr).HasMaxLength(50);

        builder.Property(booking => booking.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(booking => booking.IdempotencyKey).HasMaxLength(100).IsRequired();
        builder.Property(booking => booking.FailureReason).HasMaxLength(1000);

        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(booking => booking.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Supplier>()
            .WithMany()
            .HasForeignKey(booking => booking.SupplierId)
            .OnDelete(DeleteBehavior.Restrict);

        // SetNull: search results are disposable, a booking is not.
        builder.HasOne<SupplierOffer>()
            .WithMany()
            .HasForeignKey(booking => booking.SupplierOfferId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasMany(booking => booking.Confirmations)
            .WithOne()
            .HasForeignKey(confirmation => confirmation.SupplierBookingId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(booking => booking.Confirmations).UsePropertyAccessMode(PropertyAccessMode.Field);

        // One supplier booking per order line. This is the double-ticketing backstop that survives a
        // process dying (ADR-0003): a second worker that reaches this point for the same line cannot
        // even create the row. The FK to orders.order_lines arrives with that table (#41).
        builder.HasIndex(booking => booking.OrderLineId)
            .IsUnique()
            .HasDatabaseName("ix_supplier_bookings_order_line_id");

        // Platform-wide, not per agency: a retried saga step must land on the same row whoever runs it.
        builder.HasIndex(booking => booking.IdempotencyKey)
            .IsUnique()
            .HasDatabaseName("ix_supplier_bookings_idempotency_key");

        // What the status poller (#37) reads: bookings in a given state that are due.
        builder.HasIndex(booking => new { booking.Status, booking.NextPollAt })
            .HasDatabaseName("ix_supplier_bookings_status_next_poll_at");

        builder.HasIndex(booking => new { booking.AgencyId, booking.CreatedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_supplier_bookings_agency_id_created_at");
    }
}

public sealed class SupplierBookingConfirmationConfiguration : IEntityTypeConfiguration<SupplierBookingConfirmation>
{
    public void Configure(EntityTypeBuilder<SupplierBookingConfirmation> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("supplier_booking_confirmations", SupplierSchema.Name);
        builder.HasKey(confirmation => confirmation.Id);
        builder.Property(confirmation => confirmation.Id).ValueGeneratedNever();

        builder.Property(confirmation => confirmation.ConfirmationCode).HasMaxLength(100).IsRequired();
        builder.Property(confirmation => confirmation.HashExpected).HasMaxLength(SupplierSchema.HashLength).IsRequired();

        // Longer than a SHA-512: this is whatever the supplier sent, and a wrong-length value must be
        // stored as evidence of the mismatch rather than rejected by the column.
        builder.Property(confirmation => confirmation.HashReceived).HasMaxLength(512).IsRequired();

        builder.HasIndex(confirmation => new { confirmation.AgencyId, confirmation.SupplierBookingId })
            .HasDatabaseName("ix_supplier_booking_confirmations_agency_id_booking_id");

        builder.HasIndex(confirmation => new { confirmation.SupplierBookingId, confirmation.Sequence })
            .IsUnique()
            .HasDatabaseName("ix_supplier_booking_confirmations_booking_id_sequence");
    }
}

public sealed class SupplierBookingPassengerConfiguration : IEntityTypeConfiguration<SupplierBookingPassenger>
{
    public void Configure(EntityTypeBuilder<SupplierBookingPassenger> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("supplier_booking_passengers", SupplierSchema.Name);
        builder.HasKey(passenger => passenger.Id);
        builder.Property(passenger => passenger.Id).ValueGeneratedNever();

        builder.Property(passenger => passenger.PassengerType).HasConversion<string>().HasMaxLength(10).IsRequired();
        builder.Property(passenger => passenger.Title).HasMaxLength(20);
        builder.Property(passenger => passenger.FirstName).HasMaxLength(100).IsRequired();
        builder.Property(passenger => passenger.MiddleName).HasMaxLength(100);
        builder.Property(passenger => passenger.LastName).HasMaxLength(100).IsRequired();
        builder.Property(passenger => passenger.Gender).HasMaxLength(20);
        builder.Property(passenger => passenger.Email).HasMaxLength(320);
        builder.Property(passenger => passenger.PhoneNumber).HasMaxLength(32);
        builder.Property(passenger => passenger.SeatNumbers).HasColumnType("jsonb");
        builder.Property(passenger => passenger.TicketNumber).HasMaxLength(50);

        builder.HasOne<SupplierBooking>()
            .WithMany()
            .HasForeignKey(passenger => passenger.SupplierBookingId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(passenger => new { passenger.AgencyId, passenger.SupplierBookingId })
            .HasDatabaseName("ix_supplier_booking_passengers_agency_id_booking_id");
    }
}

public sealed class PassengerDocumentConfiguration : IEntityTypeConfiguration<PassengerDocument>
{
    public void Configure(EntityTypeBuilder<PassengerDocument> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("passenger_documents", SupplierSchema.Name);
        builder.HasKey(document => document.Id);
        builder.Property(document => document.Id).ValueGeneratedNever();

        builder.Property(document => document.DocType).HasConversion<string>().HasMaxLength(10).IsRequired();
        builder.Property(document => document.InnerDocType).HasConversion<string>().HasMaxLength(20).IsRequired();

        // Ciphertext only. There is no column a passport number could land in, in clear.
        builder.Property(document => document.DocNumberEncrypted).HasColumnType("bytea").IsRequired();

        builder.Property(document => document.IssuingCountry).HasMaxLength(2).IsFixedLength().IsRequired();
        builder.Property(document => document.NationalityCountry).HasMaxLength(2).IsFixedLength();

        builder.HasOne<SupplierBookingPassenger>()
            .WithMany()
            .HasForeignKey(document => document.PassengerId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(document => new { document.AgencyId, document.PassengerId })
            .HasDatabaseName("ix_passenger_documents_agency_id_passenger_id");
    }
}

public sealed class SupplierApiCallConfiguration : IEntityTypeConfiguration<SupplierApiCall>
{
    public void Configure(EntityTypeBuilder<SupplierApiCall> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(SupplierSchema.ApiCallsTable, SupplierSchema.Name);

        // PostgreSQL requires the partition key in every unique constraint, so the key is
        // (id, occurred_at). Ids are UUIDv7 and already time-ordered, so this costs nothing.
        builder.HasKey(call => new { call.Id, call.OccurredAt });
        builder.Property(call => call.Id).ValueGeneratedNever();

        builder.Property(call => call.Operation).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(call => call.Outcome).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(call => call.HttpMethod).HasMaxLength(10).IsRequired();
        builder.Property(call => call.Endpoint).HasMaxLength(500).IsRequired();
        builder.Property(call => call.RequestHeaders).HasColumnType("jsonb").IsRequired();

        // Text, not jsonb: a supplier in trouble returns HTML error pages, and jsonb would reject them.
        builder.Property(call => call.RequestBody).HasColumnType("text");
        builder.Property(call => call.ResponseBody).HasColumnType("text");

        builder.Property(call => call.ErrorMessage).HasMaxLength(2000);
        builder.Property(call => call.CorrelationId).HasMaxLength(100);

        builder.HasOne<Supplier>()
            .WithMany()
            .HasForeignKey(call => call.SupplierId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(call => new { call.AgencyId, call.OccurredAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_supplier_api_calls_agency_id_occurred_at");

        // The supplier error-rate report: one supplier's calls of one kind over time.
        builder.HasIndex(call => new { call.SupplierId, call.Operation, call.OccurredAt })
            .IsDescending(false, false, true)
            .HasDatabaseName("ix_supplier_api_calls_supplier_operation_occurred_at");

        // "What did we send for this booking?" — the dispute question.
        builder.HasIndex(call => new { call.SupplierBookingId, call.OccurredAt })
            .HasDatabaseName("ix_supplier_api_calls_supplier_booking_id_occurred_at");
    }
}

public sealed class SupplierStatusPollConfiguration : IEntityTypeConfiguration<SupplierStatusPoll>
{
    public void Configure(EntityTypeBuilder<SupplierStatusPoll> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("supplier_status_polls", SupplierSchema.Name);
        builder.HasKey(poll => poll.Id);
        builder.Property(poll => poll.Id).ValueGeneratedNever();

        builder.Property(poll => poll.Outcome).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(poll => poll.ActionTaken).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(poll => poll.Note).HasMaxLength(1000);

        // Restrict: a poll is the evidence a payment reversal rests on, so it outlives nothing it justifies.
        builder.HasOne<SupplierBooking>()
            .WithMany()
            .HasForeignKey(poll => poll.SupplierBookingId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(poll => new { poll.AgencyId, poll.SupplierBookingId, poll.PolledAt })
            .IsDescending(false, false, true)
            .HasDatabaseName("ix_supplier_status_polls_agency_id_booking_id_polled_at");
    }
}
