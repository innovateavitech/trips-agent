using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TripsAgent.Application.Suppliers;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Suppliers;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.Infrastructure.Suppliers;
using TripsAgent.Infrastructure.Tenancy;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Suppliers;

/// <summary>
/// The background writer against real PostgreSQL, connected as the policed application role.
/// Issue #39: calls from many agencies written off the request path, each under its own agency's
/// row-level security, redacted, and never lost without a trace.
/// </summary>
[Collection(PostgresCollection.Name)]
public class SupplierApiCallWriterTests
{
    private const string PassportNumber = "A01234567";
    private const string MerchantKey = "fake.merchant.key.value";

    private readonly PostgresFixture _postgres;

    public SupplierApiCallWriterTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task Calls_from_several_agencies_and_the_platform_are_written_each_as_its_own_agency()
    {
        var world = await WorldAsync();
        var agencyA = Guid.CreateVersion7();
        var agencyB = Guid.CreateVersion7();

        var fromA = Capture(world.SupplierId, agencyA);
        var fromB = Capture(world.SupplierId, agencyB);
        var platform = Capture(world.SupplierId, agencyId: null);

        await world.RunWriterAsync(fromA, fromB, platform);

        world.Buffer.LostCount.Should().Be(0);

        await using (var owner = _postgres.Connect(world.Database, asApplicationRole: false))
        {
            var rows = await owner.SupplierApiCalls.IgnoreQueryFilters().OrderBy(call => call.Id).ToListAsync();

            rows.Select(call => call.Id).Should().BeEquivalentTo([fromA.CallId, fromB.CallId, platform.CallId]);
            rows.Should().OnlyContain(call => !call.RequestBody!.Contains(PassportNumber) && !call.RequestHeaders.Contains(MerchantKey));
            rows.Single(call => call.Id == fromA.CallId).AgencyId.Should().Be(agencyA);
            rows.Single(call => call.Id == platform.CallId).AgencyId.Should().BeNull();
        }

        // Row-level security, with the EF filter deliberately removed: A sees its own call and nothing else.
        var tenancy = TestTenancy.For(agencyA);
        await using var asA = _postgres.Connect(world.Database, tenancy.Tenant, tenancy.Scope);

        (await asA.SupplierApiCalls.IgnoreQueryFilters().Select(call => call.Id).ToListAsync())
            .Should().Equal(fromA.CallId);
    }

    [Fact]
    public async Task A_row_the_database_refuses_is_reported_lost_without_taking_its_batch_with_it()
    {
        var world = await WorldAsync();
        var agency = Guid.CreateVersion7();

        var good = Capture(world.SupplierId, agency);
        var poison = Capture(supplierId: Guid.CreateVersion7(), agency);   // no such supplier: a foreign-key violation
        var alsoGood = Capture(world.SupplierId, agency);

        await world.RunWriterAsync(good, poison, alsoGood);

        world.Buffer.LostCount.Should().Be(1);

        await using var owner = _postgres.Connect(world.Database, asApplicationRole: false);
        (await owner.SupplierApiCalls.IgnoreQueryFilters().Select(call => call.Id).ToListAsync())
            .Should().BeEquivalentTo([good.CallId, alsoGood.CallId]);
    }

    [Fact]
    public async Task Calls_buffered_before_the_writer_ever_ran_are_still_written_when_the_host_stops()
    {
        // A host stopped moments after starting can cancel ExecuteAsync before it begins. What was
        // already buffered must still reach the table rather than vanish with the process.
        var world = await WorldAsync();
        var early = Capture(world.SupplierId, Guid.CreateVersion7());

        world.Buffer.Record(early);
        var writer = world.Writer;
        await writer.StartAsync(new CancellationToken(canceled: true));
        await writer.StopAsync(CancellationToken.None);

        world.Buffer.LostCount.Should().Be(0);

        await using var owner = _postgres.Connect(world.Database, asApplicationRole: false);
        (await owner.SupplierApiCalls.IgnoreQueryFilters().Select(call => call.Id).ToListAsync())
            .Should().Equal(early.CallId);
    }

    private static SupplierCallCapture Capture(Guid supplierId, Guid? agencyId) => new()
    {
        CallId = Guid.CreateVersion7(),
        SupplierId = supplierId,
        AgencyId = agencyId,
        Operation = SupplierOperation.Issue,
        HttpMethod = "POST",
        Endpoint = "/api/v2/ticketing/issue",
        RequestHeaders = new Dictionary<string, string> { ["MerchantKey"] = MerchantKey, ["MerchantCode"] = "ACCESS" },
        RequestBody = $$"""{"Passengers":[{"DocNumber":"{{PassportNumber}}"}]}""",
        ResponseStatusCode = 200,
        ResponseBody = """{"StatusCode":1}""",
        LatencyMs = 812,
        Outcome = SupplierCallOutcome.Succeeded,
        OccurredAt = DateTimeOffset.UtcNow,
        CorrelationId = "order-7f3a",
    };

    private async Task<World> WorldAsync([CallerMemberName] string testName = "")
    {
        var name = $"sac_{testName.ToLowerInvariant()}";
        name = name[..Math.Min(name.Length, 60)];

        await using var setup = await _postgres.CreateEmptyDatabaseAsync(name);
        await setup.Database.MigrateAsync();

        var supplier = Supplier.Register("trips_africa", "Trips Africa", SupplierKind.Multi, "https://staging.tripsafrica.test");
        setup.Suppliers.Add(supplier);
        await setup.SaveChangesAsync();

        return new World(_postgres, name, supplier.Id);
    }

    /// <summary>A database with one supplier, and the writer wired to it as production wires it.</summary>
    private sealed class World
    {
        private readonly ServiceProvider _services;

        public World(PostgresFixture postgres, string database, Guid supplierId)
        {
            Database = database;
            SupplierId = supplierId;

            var services = new ServiceCollection();
            services.AddLogging();

            // Small batches, so three calls from one agency cross a batch boundary.
            services.AddSingleton(Options.Create(new SupplierApiCallOptions { BatchSize = 2 }));
            services.AddSingleton<SupplierApiCallBuffer>();
            services.AddSingleton<SupplierApiCallWriterService>();

            services.AddScoped<TenantContext>();
            services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
            services.AddScoped<IPlatformScope>(sp => new PlatformScope(
                sp.GetRequiredService<ITenantContext>(), NullLogger<PlatformScope>.Instance));

            // As the policed application role, exactly as the Api and the Worker connect.
            services.AddScoped<AppDbContext>(sp => postgres.Connect(
                database, sp.GetRequiredService<ITenantContext>(), sp.GetRequiredService<IPlatformScope>()));

            _services = services.BuildServiceProvider();
        }

        public string Database { get; }

        public Guid SupplierId { get; }

        public SupplierApiCallBuffer Buffer => _services.GetRequiredService<SupplierApiCallBuffer>();

        public SupplierApiCallWriterService Writer => _services.GetRequiredService<SupplierApiCallWriterService>();

        /// <summary>Starts the writer, records the calls as the audit handler would, and stops it — which drains.</summary>
        public async Task RunWriterAsync(params SupplierCallCapture[] captures)
        {
            var writer = Writer;
            await writer.StartAsync(CancellationToken.None);

            foreach (var capture in captures)
            {
                Buffer.Record(capture);
            }

            await writer.StopAsync(CancellationToken.None);
        }
    }
}
