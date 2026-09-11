using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TripsAgent.Application.Retention;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Auditing;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Payments;
using TripsAgent.Domain.Pricing;
using TripsAgent.Domain.Suppliers;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Infrastructure.Messaging;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.Infrastructure.Retention;
using TripsAgent.Infrastructure.Security;
using TripsAgent.Infrastructure.Suppliers;
using TripsAgent.Infrastructure.Tenancy;
using TripsAgent.IntegrationTests.Identity;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Retention;

/// <summary>
/// The purge job against a real, migrated PostgreSQL, running as the policed application role exactly
/// as the Worker does.
/// </summary>
/// <remarks>
/// The test that matters most is the first: the job, live, with every window at one day, over a
/// database holding decade-old rows in the ledger, the audit log, orders and supplier bookings — and
/// not one of those rows may change. Deleting data cannot be undone, so that one is written to be as
/// hard on the job as the job can be configured to be.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class DataRetentionPurgeTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly DateTimeOffset TenYearsAgo = Now.AddYears(-10);

    private readonly PostgresFixture _postgres;

    public DataRetentionPurgeTests(PostgresFixture postgres) => _postgres = postgres;

    private static DataRetentionOptions DryRun => new();

    private static DataRetentionOptions Live => new() { DryRun = false };

    /// <summary>Every window at one day, live — more aggressive than validation even allows in production.</summary>
    private static DataRetentionOptions MostAggressive => new()
    {
        DryRun = false,
        LoginAttemptDays = 1,
        ExpiredCredentialDays = 1,
        ExpiredInvitationDays = 1,
        ProcessedMessageDays = 1,
        NotificationDays = 1,
        ExpiredCartDays = 1,
        TravelDocumentDays = 1,
    };

    // ------------------------------------------------------------------ the one that matters

    [Fact]
    public async Task Financial_and_audit_records_are_never_touched_even_with_every_window_at_one_day()
    {
        await using var world = await WorldAsync();

        // A decade-old sale with its supplier booking and passenger, its money and its audit trail —
        // every one of them far past any window the job has.
        var order = await world.PlaceOrderAsync("ORD-2016-000001", TenYearsAgo);
        var trip = await world.BookTripAsync(order, await world.OfferAsync(arrival: TenYearsAgo.AddDays(3)));
        await world.AddLedgerHistoryAsync(TenYearsAgo, 500_000);
        await world.AddAuditHistoryAsync(TenYearsAgo);
        var oldAttempt = await world.AddLoginAttemptAsync(TenYearsAgo);

        var before = await ProtectedFingerprintsAsync(world);

        foreach (var seeded in new[]
                 {
                     "tenancy.agencies", "orders.orders", "orders.order_lines", "pricing.price_quotes",
                     "pricing.markup_rules", "payments.wallets", "payments.ledger_accounts",
                     "payments.ledger_entries", "platform.audit_logs", "supplier.supplier_bookings",
                     "supplier.supplier_booking_passengers",
                 })
        {
            before[seeded].Should().NotBe(World.Empty, $"{seeded} has to hold old rows for this test to prove anything");
        }

        var result = await world.Purge(MostAggressive).RunAsync();

        // It really ran, live, and really deleted — an untouched ledger proves nothing otherwise.
        result.DryRun.Should().BeFalse();
        result.Tables.Should().OnlyContain(outcome => outcome.Error == null);
        (await world.CountAsync("identity.login_attempts", $"t.id = '{oldAttempt}'")).Should().Be(0);
        (await world.CountAsync("supplier.passenger_documents", $"t.id = '{trip.DocumentId}'")).Should().Be(0);

        var after = await ProtectedFingerprintsAsync(world);

        after.Should().Equal(before, "not one row of a financial, audit or supporting table may be deleted or changed");
    }

    [Fact]
    public async Task A_rule_aimed_at_a_protected_table_is_refused_before_anything_runs()
    {
        await using var world = await WorldAsync();
        var oldAttempt = await world.AddLoginAttemptAsync(Now.AddDays(-400));

        // A legitimate rule first, then one aimed at the ledger. The refusal must come before the
        // first one runs, not after — a job that deletes half its list and then stops is not safe.
        var act = () => world.Purge(
            Live,
            rules:
            [
                new RetentionRule("identity.login_attempts", RetentionAction.Delete, TimeSpan.FromDays(1), "t.attempted_at < @cutoff"),
                new RetentionRule("payments.ledger_entries", RetentionAction.Delete, TimeSpan.FromDays(1), "true"),
            ]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*payments.ledger_entries*");
        (await world.CountAsync("identity.login_attempts", $"t.id = '{oldAttempt}'")).Should().Be(1);
    }

    // ------------------------------------------------------------------ dry run, live, twice

    [Fact]
    public async Task A_dry_run_reports_what_it_would_delete_and_deletes_nothing()
    {
        await using var world = await WorldAsync();
        await SeedOperationalAsync(world);
        var before = await OperationalCountsAsync(world);

        var result = await world.Purge(DryRun).RunAsync();

        result.DryRun.Should().BeTrue();
        result.Tables.Should().OnlyContain(outcome => outcome.Error == null, "every rule's SQL has to run cleanly against the real schema");
        result.For("identity.login_attempts").Rows.Should().Be(2);
        result.For("identity.refresh_tokens").Rows.Should().Be(2);
        result.For("platform.outbox_messages").Rows.Should().Be(1, "a failed message waits for a person and is not purged");
        result.For("platform.inbox_messages").Rows.Should().Be(1);
        result.For("orders.carts").Rows.Should().Be(1);

        (await OperationalCountsAsync(world)).Should().Equal(before, "a dry run deletes nothing");
    }

    [Fact]
    public async Task A_live_run_deletes_what_is_past_its_window_and_keeps_everything_else()
    {
        await using var world = await WorldAsync();
        var seeded = await SeedOperationalAsync(world);

        var result = await world.Purge(Live).RunAsync();

        result.Tables.Should().OnlyContain(outcome => outcome.Error == null);
        result.For("identity.login_attempts").Rows.Should().Be(2);

        (await world.CountAsync("identity.login_attempts")).Should().Be(1);
        (await world.CountAsync("identity.login_attempts", $"t.id = '{seeded.RecentAttempt}'")).Should().Be(1);

        (await world.CountAsync("platform.outbox_messages", $"t.id = '{seeded.OldDispatched}'")).Should().Be(0);
        (await world.CountAsync("platform.outbox_messages", $"t.id = '{seeded.OldFailed}'")).Should().Be(1);
        (await world.CountAsync("platform.outbox_messages", $"t.id = '{seeded.RecentDispatched}'")).Should().Be(1);

        (await world.CountAsync("platform.inbox_messages", $"t.message_id = '{seeded.OldInbox}'")).Should().Be(0);
        (await world.CountAsync("platform.inbox_messages", $"t.message_id = '{seeded.RecentInbox}'")).Should().Be(1);

        // A rotation chain goes whole, despite the RESTRICT key from a used token to its replacement.
        (await world.CountAsync("identity.refresh_tokens", $"t.id IN ('{seeded.Tokens.Used}', '{seeded.Tokens.Replacement}')")).Should().Be(0);
        (await world.CountAsync("identity.refresh_tokens", $"t.id = '{seeded.Tokens.Live}'")).Should().Be(1);

        (await world.CountAsync("orders.carts", $"t.id = '{seeded.ExpiredCart}'")).Should().Be(0);
        (await world.CountAsync("orders.carts", $"t.id = '{seeded.ActiveCart}'")).Should().Be(1);
    }

    [Fact]
    public async Task Running_twice_in_a_day_deletes_nothing_extra_and_errors_nowhere()
    {
        await using var world = await WorldAsync();
        await SeedOperationalAsync(world);

        var first = await world.Purge(Live).RunAsync();
        var afterFirst = await OperationalCountsAsync(world);

        var second = await world.Purge(Live).RunAsync();

        first.Tables.Sum(outcome => outcome.Rows).Should().BeGreaterThan(0);
        second.Tables.Should().OnlyContain(outcome => outcome.Rows == 0 && outcome.Error == null);
        (await OperationalCountsAsync(world)).Should().Equal(afterFirst);
    }

    [Fact]
    public async Task Every_run_writes_an_audit_row_per_table_with_the_row_count_and_window()
    {
        await using var world = await WorldAsync();
        await SeedOperationalAsync(world);

        var dry = await world.Purge(DryRun).RunAsync();
        var live = await world.Purge(Live).RunAsync();

        foreach (var (run, expectedAction) in new[] { (dry, "retention.dry_run"), (live, (string?)null) })
        {
            var rows = await world.Owner.AuditLogs.AsNoTracking()
                .Where(entry => entry.CorrelationId == run.RunId.ToString())
                .ToListAsync();

            rows.Should().HaveCount(run.Tables.Count, "one audit row per table, every run");
            rows.Select(row => row.EntityId).Should().BeEquivalentTo(run.Tables.Select(outcome => outcome.Table));

            foreach (var row in rows)
            {
                var outcome = run.For(row.EntityId);

                row.EntityType.Should().Be(DataRetentionPurge.AuditEntityType);
                row.ActorType.Should().Be(AuditActorType.System);
                row.Action.Should().Be(expectedAction ?? DataRetentionPurge.ActionName(outcome, dryRun: false));

                using var state = JsonDocument.Parse(row.AfterState!);
                state.RootElement.GetProperty("table").GetString().Should().Be(outcome.Table);
                state.RootElement.GetProperty("rows").GetInt32().Should().Be(outcome.Rows);
                state.RootElement.GetProperty("window").GetString().Should().Be(outcome.Window).And.NotBeNullOrWhiteSpace();
                state.RootElement.GetProperty("dryRun").GetBoolean().Should().Be(run.DryRun);
            }
        }

        live.For("identity.login_attempts").Window.Should().Be("90 days");
    }

    // ------------------------------------------------------------------ travel documents

    [Fact]
    public async Task Travel_documents_go_a_set_interval_after_the_trip_and_are_kept_while_the_trip_date_is_unknown()
    {
        await using var world = await WorldAsync();

        var over = await world.BookTripAsync(
            await world.PlaceOrderAsync("ORD-2026-000001", Now.AddDays(-230)),
            await world.OfferAsync(arrival: Now.AddDays(-200)));
        var ahead = await world.BookTripAsync(
            await world.PlaceOrderAsync("ORD-2026-000002", Now.AddDays(-5)),
            await world.OfferAsync(arrival: Now.AddDays(30)));
        var unknown = await world.BookTripAsync(
            await world.PlaceOrderAsync("ORD-2026-000003", Now.AddDays(-400)),
            offerId: null);

        var result = await world.Purge(Live).RunAsync();

        result.For("supplier.passenger_documents").Rows.Should().Be(1);
        result.For("orders.order_travellers").Rows.Should().Be(1);

        (await world.CountAsync("supplier.passenger_documents", $"t.id = '{over.DocumentId}'")).Should().Be(0);
        (await world.CountAsync("supplier.passenger_documents", $"t.id = '{ahead.DocumentId}'")).Should().Be(1, "the trip has not happened yet");
        (await world.CountAsync("supplier.passenger_documents", $"t.id = '{unknown.DocumentId}'")).Should().Be(1, "unknown means keep, never purge");

        // The traveller stays with the order; only the document details are cleared.
        (await world.CountAsync(
                "orders.order_travellers",
                $"t.id = '{over.TravellerId}' AND t.passport_number_encrypted IS NULL AND t.passport_expiry IS NULL AND t.first_name = 'Ngozi'"))
            .Should().Be(1);
        (await world.CountAsync("orders.order_travellers", $"t.id IN ('{ahead.TravellerId}', '{unknown.TravellerId}') AND t.passport_number_encrypted IS NOT NULL"))
            .Should().Be(2);

        // Who was ticketed is part of the booking record and is never this job's to remove.
        (await world.CountAsync("supplier.supplier_booking_passengers")).Should().Be(3);

        var again = await world.Purge(Live).RunAsync();
        again.For("supplier.passenger_documents").Rows.Should().Be(0);
        again.For("orders.order_travellers").Rows.Should().Be(0, "a cleared traveller no longer matches");
    }

    // ------------------------------------------------------------------ supplier call log

    [Fact]
    public async Task Supplier_call_partitions_past_the_window_are_reported_and_a_live_run_drops_them()
    {
        await using var world = await WorldAsync();
        var supplierId = await world.SupplierIdAsync();

        var month = new DateOnly(Now.Year, Now.Month, 1).AddMonths(-6);
        var partition = await world.CreateSupplierApiCallPartitionAsync(month);
        await world.AddSupplierApiCallAsync(supplierId, new DateTimeOffset(month.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(2));

        var dry = await world.Purge(DryRun).RunAsync();

        dry.For(DataRetentionPurge.SupplierApiCallsTable).Rows.Should().Be(1);
        dry.For(DataRetentionPurge.SupplierApiCallsTable).Window.Should().Be("3 whole months");
        (await world.PartitionExistsAsync(partition)).Should().BeTrue("this job drops nothing in a dry run");

        var live = await world.Purge(Live).RunAsync();

        live.For(DataRetentionPurge.SupplierApiCallsTable).Rows.Should().Be(1);
        (await world.PartitionExistsAsync(partition)).Should().BeFalse("a live run calls the partition maintenance, which drops it");
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<Dictionary<string, string>> ProtectedFingerprintsAsync(World world)
    {
        var fingerprints = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var table in RetentionCatalogue.ProtectedTables.Order(StringComparer.Ordinal))
        {
            // The job adds audit rows of its own. Everything else in the log must be exactly as it was.
            var where = table == "platform.audit_logs"
                ? $"t.entity_type <> '{DataRetentionPurge.AuditEntityType}'"
                : "true";

            fingerprints[table] = await world.FingerprintAsync(table, where);
        }

        return fingerprints;
    }

    private static async Task<Dictionary<string, long>> OperationalCountsAsync(World world)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var table in new[]
                 {
                     "identity.login_attempts", "identity.refresh_tokens", "platform.outbox_messages",
                     "platform.inbox_messages", "orders.carts",
                 })
        {
            counts[table] = await world.CountAsync(table);
        }

        return counts;
    }

    /// <summary>
    /// Rows either side of each window, so every rule has something to delete and something to keep.
    /// With the default windows: two login attempts past 90 days, one inside; a dispatched outbox
    /// message past 30 days, a failed one as old, a recent one; and so on.
    /// </summary>
    private static async Task<Operational> SeedOperationalAsync(World world) =>
        new(
            RecentAttempt: await world.AddLoginAttemptAsync(Now.AddDays(-1)),
            OldAttempts:
            [
                await world.AddLoginAttemptAsync(Now.AddDays(-200)),
                await world.AddLoginAttemptAsync(Now.AddDays(-100)),
            ],
            OldDispatched: await world.AddOutboxMessageAsync(Now.AddDays(-100), OutboxMessageStatus.Dispatched),
            OldFailed: await world.AddOutboxMessageAsync(Now.AddDays(-100), OutboxMessageStatus.Failed),
            RecentDispatched: await world.AddOutboxMessageAsync(Now.AddDays(-1), OutboxMessageStatus.Dispatched),
            OldInbox: await world.AddInboxMessageAsync(Now.AddDays(-100)),
            RecentInbox: await world.AddInboxMessageAsync(Now.AddDays(-1)),
            Tokens: await world.AddRefreshTokenChainAsync(),
            ExpiredCart: await world.OpenCartAsync(Now.AddDays(-100)),
            ActiveCart: await world.OpenCartAsync(Now));

    private async Task<World> WorldAsync([CallerMemberName] string testName = "")
    {
        _ = testName;

        // A fresh name every time. See RegistrationEndToEndTests.
        var database = $"retention_{Guid.NewGuid():N}";
        Guid agencyId;

        await using (var setup = await _postgres.CreateEmptyDatabaseAsync(database))
        {
            await setup.Database.MigrateAsync();

            var agency = Agency.RegisterPrincipal("Lagos Travel Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos");
            setup.Agencies.Add(agency);
            await setup.SaveChangesAsync();
            agencyId = agency.Id;
        }

        return new World(_postgres, database, agencyId);
    }

    private sealed record Operational(
        Guid RecentAttempt,
        IReadOnlyList<Guid> OldAttempts,
        Guid OldDispatched,
        Guid OldFailed,
        Guid RecentDispatched,
        Guid OldInbox,
        Guid RecentInbox,
        (Guid Used, Guid Replacement, Guid Live) Tokens,
        Guid ExpiredCart,
        Guid ActiveCart);

    private sealed record Trip(Guid BookingId, Guid PassengerId, Guid DocumentId, Guid TravellerId);

    /// <summary>An outbox payload. What it says does not matter; only when it was dispatched does.</summary>
    private sealed record RetentionProbe(int Value);

    private sealed class World : IAsyncDisposable
    {
        public const string Empty = "empty";

        private static readonly AesGcmSecretProtector Protector =
            new(RandomNumberGenerator.GetBytes(AesGcmSecretProtector.KeyBytes));

        private static readonly SupplierApiCallOptions SupplierApiCalls = new() { RetentionMonths = 3, PartitionsCreatedAhead = 1 };

        private readonly PostgresFixture _postgres;
        private readonly List<AppDbContext> _contexts = [];
        private readonly (TenantContext Tenant, PlatformScope Scope) _ownerTenancy = TestTenancy.None();

        private Guid? _supplierId;
        private MarkupRule? _rule;

        public World(PostgresFixture postgres, string database, Guid agencyId)
        {
            _postgres = postgres;
            Database = database;
            AgencyId = agencyId;
            Owner = Track(postgres.Connect(database, _ownerTenancy.Tenant, _ownerTenancy.Scope, asApplicationRole: false));
        }

        public string Database { get; }

        public Guid AgencyId { get; }

        /// <summary>The schema owner: for setup and for checking, never for the job itself.</summary>
        public AppDbContext Owner { get; }

        /// <summary>
        /// The job as the Worker builds it: the policed application role, no tenant, its own platform
        /// scope — and the real supplier call log maintenance on the owner's connection.
        /// </summary>
        public DataRetentionPurge Purge(DataRetentionOptions options, IReadOnlyList<RetentionRule>? rules = null)
        {
            var none = TestTenancy.None();
            var db = Track(_postgres.Connect(Database, none.Tenant, none.Scope));
            var maintenance = new SupplierApiCallPartitionMaintenance(Owner, Options.Create(SupplierApiCalls));

            return new DataRetentionPurge(
                db,
                none.Scope,
                maintenance,
                options,
                SupplierApiCalls,
                TimeProvider.System,
                NullLogger<DataRetentionPurge>.Instance,
                rules);
        }

        public async Task<long> CountAsync(string table, string where = "true")
        {
            var sql = $"SELECT count(*) AS \"Value\" FROM {table} AS t WHERE {where}";
            return await Owner.Database.SqlQueryRaw<long>(sql).SingleAsync();
        }

        /// <summary>Every row, every column, hashed — so a change to any value shows, not only a deletion.</summary>
        public async Task<string> FingerprintAsync(string table, string where)
        {
            var sql =
                $"SELECT coalesce(md5(string_agg(t::text, '|' ORDER BY t::text)), '{Empty}') AS \"Value\" FROM {table} AS t WHERE {where}";
            return await Owner.Database.SqlQueryRaw<string>(sql).SingleAsync();
        }

        // -------------------------------------------------------------- sales and bookings

        public async Task<Order> PlaceOrderAsync(string orderNumber, DateTimeOffset at)
        {
            var db = AsAgency(at);

            if (_rule is null)
            {
                _rule = MarkupRule.Create(AgencyId, new MarkupRuleTerms
                {
                    Scope = MarkupScope.Global,
                    Currency = "NGN",
                    CalculationType = MarkupCalculationType.Percentage,
                    PercentBasisPoints = 1_000,
                    EffectiveFrom = TenYearsAgo.AddDays(-1),
                });
                db.MarkupRules.Add(_rule);
                await db.SaveChangesAsync();
            }

            var quote = PriceQuote.Record(
                AgencyId,
                new PricingSubject(PricedProductType.Flight, "NGN"),
                new PriceBreakdown(
                    new Money(100_000), new Money(10_000), new Money(750), new Money(500), new Money(110_750),
                    "NGN", new MarkupRuleDefinition(_rule.Id, AgencyId, _rule.Terms), false, 750, 0),
                at,
                TimeSpan.FromMinutes(30));
            db.PriceQuotes.Add(quote);
            await db.SaveChangesAsync();

            var line = OrderLine.FromQuote(quote, "LOS → ABV, Air Peace", """{"adults":1}""", at);
            var order = Order.Place(AgencyId, orderNumber, "NGN", BuyerType.AgentAssisted, OrderChannel.Console, null, [line], at);
            db.Orders.Add(order);
            await db.SaveChangesAsync();

            return order;
        }

        /// <summary>An offer whose last flight lands at <paramref name="arrival"/>, or null for a trip with no known dates.</summary>
        public async Task<Guid?> OfferAsync(DateTimeOffset? arrival)
        {
            if (arrival is not { } lands)
            {
                return null;
            }

            var supplierId = await SupplierIdAsync();
            var db = AsAgency();

            var request = SearchRequest.Start(
                AgencyId, null, SupplierProductType.Flight,
                Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), """{"from":"LOS","to":"ABV"}""", "OneWay", Now);
            db.SearchRequests.Add(request);
            await db.SaveChangesAsync();

            var session = SearchSession.Open(AgencyId, request.Id, supplierId, $"session-{Guid.NewGuid():N}", null, Now, Now.AddHours(1));
            db.SearchSessions.Add(session);
            await db.SaveChangesAsync();

            var offer = SupplierOffer.Record(
                AgencyId, session.Id, supplierId, SupplierProductType.Flight, $"offer-{Guid.NewGuid():N}",
                new SupplierOfferReference(), "NGN", new Money(90_000), new Money(100_000), "{}", Now, Now.AddHours(1));
            db.SupplierOffers.Add(offer);
            await db.SaveChangesAsync();

            db.FlightSegments.Add(FlightSegment.Create(
                AgencyId, offer.Id, 0, 0, "P4", null, "7100", "LOS", "ABV", lands.AddHours(-1), lands));
            await db.SaveChangesAsync();

            return offer.Id;
        }

        public async Task<Trip> BookTripAsync(Order order, Guid? offerId)
        {
            var supplierId = await SupplierIdAsync();
            var line = order.Lines[0];
            var db = AsAgency();

            var booking = SupplierBooking.Create(
                AgencyId, supplierId, line.Id, offerId, SupplierProductType.Flight, "Domestic", "Flight",
                "session-1", "NGN", $"idem-{Guid.NewGuid():N}");
            db.SupplierBookings.Add(booking);
            await db.SaveChangesAsync();

            var passenger = SupplierBookingPassenger.Add(AgencyId, booking.Id, PassengerType.Adult, "Ngozi", "Adeyemi");
            db.SupplierBookingPassengers.Add(passenger);
            await db.SaveChangesAsync();

            var document = PassengerDocument.Add(
                AgencyId, passenger.Id, TravelDocumentRecord.Docs, TravelDocumentKind.Passport,
                Protector.Protect("A01234567", "supplier.passenger_documents.doc_number"),
                "NG", "NG", new DateOnly(2020, 1, 1), new DateOnly(2030, 1, 1));
            db.PassengerDocuments.Add(document);

            var traveller = OrderTraveller.Record(
                AgencyId, line.Id, TravellerType.Adult, "Ngozi", "Adeyemi",
                passportNumberEncrypted: Protector.Protect("A01234567", "orders.order_travellers.passport_number"),
                passportExpiry: new DateOnly(2030, 1, 1),
                nationality: "NG");
            db.OrderTravellers.Add(traveller);
            await db.SaveChangesAsync();

            // Tie the line to its booking, as fulfilment will. Not a money column, so the price-final
            // trigger allows it.
            await Owner.Database.ExecuteSqlRawAsync(
                "UPDATE orders.order_lines SET supplier_booking_id = {0} WHERE id = {1}", booking.Id, line.Id);

            return new Trip(booking.Id, passenger.Id, document.Id, traveller.Id);
        }

        public async Task<Guid> SupplierIdAsync()
        {
            if (_supplierId is { } existing)
            {
                return existing;
            }

            var supplier = Supplier.Register("trips_africa", "Trips Africa", SupplierKind.Multi, "https://api.staging.trips.ng");
            Owner.Suppliers.Add(supplier);
            await Owner.SaveChangesAsync();

            _supplierId = supplier.Id;
            return supplier.Id;
        }

        // -------------------------------------------------------------- money and audit

        /// <summary>A wallet, its ledger account, and one balanced top-up dated <paramref name="at"/>.</summary>
        public async Task AddLedgerHistoryAsync(DateTimeOffset at, long amountMinor)
        {
            Guid walletAccountId;
            Guid clearingAccountId;

            using (_ownerTenancy.Scope.Enter("test setup — a wallet and its ledger accounts"))
            {
                Owner.Wallets.Add(Wallet.OpenFor(AgencyId, "NGN"));

                var wallet = LedgerAccount.ForAgency(AgencyId, LedgerAccountType.AgencyWallet, "NGN", "Wallet");
                Owner.LedgerAccounts.Add(wallet);
                await Owner.SaveChangesAsync();

                walletAccountId = wallet.Id;
                clearingAccountId = (await Owner.LedgerAccounts.SingleAsync(account =>
                    account.AgencyId == null
                    && account.AccountType == LedgerAccountType.GatewayClearing
                    && account.Currency == "NGN")).Id;
            }

            // Entries are append-only and written past the domain here only to backdate them.
            await using var connection = new NpgsqlConnection(_postgres.ConnectionStringFor(Database, asApplicationRole: false));
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            var group = Guid.CreateVersion7();
            await InsertEntryAsync(connection, transaction, group, clearingAccountId, agencyId: null, "Debit", amountMinor, at);
            await InsertEntryAsync(connection, transaction, group, walletAccountId, AgencyId, "Credit", amountMinor, at);

            await transaction.CommitAsync();
        }

        public async Task AddAuditHistoryAsync(DateTimeOffset at)
        {
            var month = DateOnly.FromDateTime(at.UtcDateTime);
            await Owner.Database
                .SqlQuery<string>($"SELECT platform.create_audit_log_partition({month}) AS \"Value\"")
                .SingleAsync();

            Owner.AuditLogs.Add(new AuditLogEntry
            {
                OccurredAt = at,
                ActorType = AuditActorType.PlatformAdmin,
                Action = "wallet.manually_adjusted",
                EntityType = "Wallet",
                EntityId = Guid.CreateVersion7().ToString(),
                Reason = "A correction made ten years ago",
            });
            await Owner.SaveChangesAsync();
        }

        // -------------------------------------------------------------- operational rows

        public async Task<Guid> AddLoginAttemptAsync(DateTimeOffset at)
        {
            var attempt = LoginAttempt.Record($"{Guid.NewGuid():N}@example.test", succeeded: false, at, ipAddress: "203.0.113.9");
            Owner.LoginAttempts.Add(attempt);
            await Owner.SaveChangesAsync();

            return attempt.Id;
        }

        public async Task<Guid> AddOutboxMessageAsync(DateTimeOffset at, string status)
        {
            var message = OutboxMessage.Create(new RetentionProbe(1), agencyId: null, at);

            if (status == OutboxMessageStatus.Dispatched)
            {
                message.MarkDispatched(at);
            }

            Owner.OutboxMessages.Add(message);
            await Owner.SaveChangesAsync();

            if (status == OutboxMessageStatus.Failed)
            {
                await Owner.Database.ExecuteSqlRawAsync(
                    "UPDATE platform.outbox_messages SET status = {0} WHERE id = {1}", OutboxMessageStatus.Failed, message.Id);
            }

            return message.Id;
        }

        public async Task<Guid> AddInboxMessageAsync(DateTimeOffset processedAt)
        {
            var messageId = Guid.NewGuid();
            Owner.InboxMessages.Add(new InboxMessage(messageId, "retention-test-consumer", processedAt));
            await Owner.SaveChangesAsync();

            return messageId;
        }

        /// <summary>A used token and the one it was exchanged for, both long expired, and a live one.</summary>
        public async Task<(Guid Used, Guid Replacement, Guid Live)> AddRefreshTokenChainAsync()
        {
            var user = User.ForAgency(AgencyId, $"{Guid.NewGuid():N}@lagos-travel.test", "not-a-real-hash", "Ada", "Obi");
            Owner.Users.Add(user);
            await Owner.SaveChangesAsync();

            var issued = Now.AddDays(-400);
            var replacement = RefreshToken.Issue(user.Id, NewHash(), issued.AddDays(1).Add(RefreshToken.Lifetime));
            var used = RefreshToken.Issue(user.Id, NewHash(), issued.Add(RefreshToken.Lifetime));
            used.MarkReplacedBy(replacement.Id, issued.AddDays(1));
            var live = RefreshToken.Issue(user.Id, NewHash(), Now.Add(RefreshToken.Lifetime));

            Owner.RefreshTokens.AddRange(replacement, used, live);
            await Owner.SaveChangesAsync();

            return (used.Id, replacement.Id, live.Id);
        }

        public async Task<Guid> OpenCartAsync(DateTimeOffset openedAt)
        {
            var db = AsAgency(openedAt);
            var cart = Cart.Open(AgencyId, "NGN", openedAt, TimeSpan.FromDays(7), sessionToken: $"guest-{Guid.NewGuid():N}");
            db.Carts.Add(cart);
            await db.SaveChangesAsync();

            return cart.Id;
        }

        // -------------------------------------------------------------- supplier call log

        public Task<string> CreateSupplierApiCallPartitionAsync(DateOnly month) =>
            Owner.Database
                .SqlQuery<string>($"SELECT supplier.create_supplier_api_call_partition({month}) AS \"Value\"")
                .SingleAsync();

        public async Task AddSupplierApiCallAsync(Guid supplierId, DateTimeOffset occurredAt) =>
            await Owner.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO supplier.supplier_api_calls
                    (id, occurred_at, agency_id, supplier_id, operation, http_method, endpoint, request_headers, latency_ms, outcome)
                VALUES ({0}, {1}, NULL, {2}, 'Search', 'POST', '/api/v2/flights/search', '{{}}'::jsonb, 120, 'Success')
                """,
                Guid.CreateVersion7(), occurredAt, supplierId);

        public async Task<bool> PartitionExistsAsync(string partition) =>
            await Owner.Database
                .SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM pg_class WHERE relname = {partition}")
                .SingleAsync() > 0;

        public async ValueTask DisposeAsync()
        {
            foreach (var context in _contexts)
            {
                await context.DisposeAsync();
            }
        }

        /// <summary>As the agency, under the policed application role — how the application writes.</summary>
        private AppDbContext AsAgency(DateTimeOffset? at = null)
        {
            var tenancy = TestTenancy.For(AgencyId);

            return Track(_postgres.Connect(
                Database,
                tenancy.Tenant,
                tenancy.Scope,
                clock: at is { } fixedAt ? new ManualClock(fixedAt) : null));
        }

        private AppDbContext Track(AppDbContext context)
        {
            _contexts.Add(context);
            return context;
        }

        private static string NewHash() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

        private static async Task InsertEntryAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            Guid group,
            Guid accountId,
            Guid? agencyId,
            string direction,
            long amountMinor,
            DateTimeOffset occurredAt)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO payments.ledger_entries
                    (id, transaction_group_id, account_id, agency_id, direction, amount_minor,
                     reference_type, reference_id, description, occurred_at)
                VALUES ($1, $2, $3, $4, $5, $6, 'test', NULL, 'a ten-year-old top-up', $7)
                """;

            command.Parameters.AddWithValue(Guid.CreateVersion7());
            command.Parameters.AddWithValue(group);
            command.Parameters.AddWithValue(accountId);
            command.Parameters.AddWithValue(agencyId is { } agency ? agency : DBNull.Value);
            command.Parameters.AddWithValue(direction);
            command.Parameters.AddWithValue(amountMinor);
            command.Parameters.AddWithValue(occurredAt);

            await command.ExecuteNonQueryAsync();
        }
    }
}
