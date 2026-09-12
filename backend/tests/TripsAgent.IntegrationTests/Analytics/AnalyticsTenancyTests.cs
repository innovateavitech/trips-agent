using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TripsAgent.Domain.Analytics;
using TripsAgent.Domain.Auditing;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Analytics;

/// <summary>
/// Who may read the analytics tables, asserted twice: once through the EF query filter and once
/// through row-level security, with the filter taken out of the way.
/// </summary>
/// <remarks>
/// Analytics is where a leak would be worst. A single row of <c>agg_platform_daily</c> tells an
/// agency what every other agency sold that day, and an export crosses tenants by design. ADR-0006
/// says the database enforces the same rule the application does, and these tests are what proves
/// the second half of that rather than assuming it.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class AnalyticsTenancyTests
{
    /// <summary>PostgreSQL's <c>restrict_violation</c>, which the append-only trigger raises.</summary>
    private const string RestrictViolation = "23001";

    private static readonly DateTimeOffset Now = new(2026, 3, 10, 9, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _postgres;

    public AnalyticsTenancyTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task An_agency_sees_its_own_bookings_and_nobody_elses()
    {
        await using var world = await AnalyticsWorld.CreateAsync(_postgres, Now, "analytics_tenancy");
        var lagos = await world.AddAgencyAsync("Lagos Travel Limited", "lagos-travel");
        var kano = await world.AddAgencyAsync("Kano Journeys", "kano-journeys");

        await world.PlaceOrderAsync(lagos, "ORD-1", Now);
        await world.PlaceOrderAsync(kano, "ORD-2", Now);
        await world.Rollup.RunFullRebuildAsync();

        await using var asLagos = world.AsAgency(lagos);

        var facts = await asLagos.BookingFacts.ToListAsync();
        facts.Should().ContainSingle();
        facts[0].AgencyId.Should().Be(lagos);

        var days = await asLagos.AgencyDailyAggregates.ToListAsync();
        days.Should().ContainSingle();
        days[0].AgencyId.Should().Be(lagos);
    }

    [Fact]
    public async Task An_agency_cannot_read_the_platforms_own_aggregates()
    {
        await using var world = await AnalyticsWorld.CreateAsync(_postgres, Now, "analytics_platform_hidden");
        var lagos = await world.AddAgencyAsync("Lagos Travel Limited", "lagos-travel");
        var supplier = await world.AddSupplierAsync("trips_africa", "Trips Africa");
        await world.EnsureSupplierCallPartitionAsync(new DateOnly(Now.Year, Now.Month, 1));

        await world.PlaceOrderAsync(lagos, "ORD-1", Now);
        await world.RecordSupplierCallAsync(
            supplier, lagos, TripsAgent.Domain.Suppliers.SupplierOperation.Search,
            TripsAgent.Domain.Suppliers.SupplierCallOutcome.Succeeded, 400, Now);

        await world.Rollup.RunFullRebuildAsync();

        // There are rows — the platform can see them.
        await using (var platform = world.AsPlatform())
        {
            (await platform.PlatformDailyAggregates.CountAsync()).Should().BeGreaterThan(0);
            (await platform.SupplierDailyAggregates.CountAsync()).Should().BeGreaterThan(0);
            (await platform.RollupRuns.CountAsync()).Should().BeGreaterThan(0);
        }

        await using var asLagos = world.AsAgency(lagos);

        (await asLagos.PlatformDailyAggregates.CountAsync()).Should().Be(0);
        (await asLagos.SupplierDailyAggregates.CountAsync()).Should().Be(0);
        (await asLagos.RollupRuns.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_database_refuses_the_platform_aggregates_even_without_the_query_filter()
    {
        await using var world = await AnalyticsWorld.CreateAsync(_postgres, Now, "analytics_rls");
        var lagos = await world.AddAgencyAsync("Lagos Travel Limited", "lagos-travel");
        await world.PlaceOrderAsync(lagos, "ORD-1", Now);
        await world.Rollup.RunFullRebuildAsync();

        // Raw SQL as the policed application role, with the agency set the way the session
        // interceptor sets it. No EF filter is involved at all: what comes back is what the policy
        // allows, and nothing else.
        await using var connection = new NpgsqlConnection(
            _postgres.ConnectionStringFor(world.Database, asApplicationRole: true));
        await connection.OpenAsync();

        await using (var setTenant = connection.CreateCommand())
        {
            setTenant.CommandText = "SELECT set_config('app.agency_id', $1, false)";
            setTenant.Parameters.AddWithValue(lagos.ToString());
            await setTenant.ExecuteNonQueryAsync();
        }

        (await CountAsync(connection, "analytics.agg_platform_daily")).Should().Be(0);
        (await CountAsync(connection, "analytics.agg_supplier_daily")).Should().Be(0);
        (await CountAsync(connection, "analytics.rollup_runs")).Should().Be(0);

        // The agency's own rows are still there, so this is a policy and not an empty database.
        (await CountAsync(connection, "analytics.fact_bookings")).Should().Be(1);
        (await CountAsync(connection, "analytics.agg_agency_daily")).Should().Be(1);
    }

    [Fact]
    public async Task A_platform_report_run_is_invisible_to_every_agency()
    {
        await using var world = await AnalyticsWorld.CreateAsync(_postgres, Now, "analytics_report_scope");
        var lagos = await world.AddAgencyAsync("Lagos Travel Limited", "lagos-travel");

        await using (var platform = world.AsPlatform())
        {
            platform.ReportJobs.Add(ReportJob.Request(
                ReportCatalog.PlatformGmvDaily,
                ReportScope.Platform,
                ReportRunMode.Asynchronous,
                ReportFormat.Csv,
                agencyId: null,
                requestedByUserId: null,
                new DateOnly(2026, 1, 1),
                new DateOnly(2026, 3, 1),
                "Platform GMV, 1 Jan 2026 to 1 Mar 2026, every agency",
                Now));

            await platform.SaveChangesAsync();
        }

        await using var asLagos = world.AsAgency(lagos);

        (await asLagos.ReportJobs.CountAsync()).Should().Be(0, "a null agency never equals anybody's");
    }

    [Fact]
    public async Task An_export_record_cannot_be_rewritten_even_by_the_owner()
    {
        await using var world = await AnalyticsWorld.CreateAsync(_postgres, Now, "analytics_export_audit");
        var lagos = await world.AddAgencyAsync("Lagos Travel Limited", "lagos-travel");

        Guid entryId;
        await using (var platform = world.AsPlatform())
        {
            var entry = ReportExportAudit.Record(
                reportJobId: null,
                ReportCatalog.AgencySalesDaily,
                ReportScope.Agency,
                ReportFormat.Csv,
                lagos,
                actorUserId: null,
                AuditActorType.System,
                actorIpAddress: null,
                "Daily sales, 1 Mar 2026 to 10 Mar 2026, Lagos Travel Limited",
                rowCount: 10,
                Now);

            platform.ReportExportAudits.Add(entry);
            await platform.SaveChangesAsync();
            entryId = entry.Id;
        }

        var edit = () => world.Owner.Database.ExecuteSqlRawAsync(
            "UPDATE analytics.report_exports_audit SET row_count = 0 WHERE id = {0}", entryId);
        var erase = () => world.Owner.Database.ExecuteSqlRawAsync(
            "DELETE FROM analytics.report_exports_audit WHERE id = {0}", entryId);

        (await edit.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(RestrictViolation);
        (await erase.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(RestrictViolation);
    }

    private static async Task<long> CountAsync(NpgsqlConnection connection, string table)
    {
        // The table name comes from a constant in this file, never from input.
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM {table}";
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
