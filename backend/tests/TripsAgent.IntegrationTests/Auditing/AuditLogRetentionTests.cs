using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TripsAgent.Infrastructure.Auditing;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Auditing;

/// <summary>
/// Retention: months are prepared ahead and expire whole.
///
/// The window is a number in configuration, not a constant in SQL, because how long Nigerian law
/// requires audit history to be kept is still open — open question 26 in the delivery plan.
/// These tests prove the mechanism honours whatever number it is given.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AuditLogRetentionTests
{
    private readonly PostgresFixture _postgres;

    public AuditLogRetentionTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task Creating_a_partition_twice_is_harmless()
    {
        // The job runs on a schedule and may overlap itself; the second run must not fail.
        await using var context = await MigratedAsync();
        var month = new DateOnly(2031, 1, 1);

        var first = await CreatePartitionAsync(context, month);
        var second = await CreatePartitionAsync(context, month);

        first.Should().Be("audit_logs_2031_01");
        second.Should().Be(first);
    }

    [Fact]
    public async Task A_partition_older_than_the_window_is_dropped()
    {
        await using var context = await MigratedAsync();

        var ancient = DateOnly.FromDateTime(TimeProvider.System.GetUtcNow().UtcDateTime).AddMonths(-200);
        var name = await CreatePartitionAsync(context, ancient);

        var dropped = await DropExpiredAsync(context, retainMonths: 12);

        dropped.Should().BeGreaterThan(0);
        (await AuditDatabase.PartitionNamesAsync(context)).Should().NotContain(name);
    }

    [Fact]
    public async Task A_partition_inside_the_window_is_kept()
    {
        await using var context = await MigratedAsync();

        var lastMonth = DateOnly.FromDateTime(TimeProvider.System.GetUtcNow().UtcDateTime).AddMonths(-1);
        var name = await CreatePartitionAsync(context, lastMonth);

        await DropExpiredAsync(context, retainMonths: 84);

        (await AuditDatabase.PartitionNamesAsync(context)).Should().Contain(name);
    }

    [Fact]
    public async Task A_retention_of_zero_is_refused()
    {
        // Zero would drop the current month while rows are still being written into it.
        await using var context = await MigratedAsync();

        var drop = async () => await DropExpiredAsync(context, retainMonths: 0);

        (await drop.Should().ThrowAsync<Exception>())
            .Which.Message.Should().Contain("retain_months must be at least 1");
    }

    [Fact]
    public async Task The_maintenance_run_prepares_the_configured_number_of_months()
    {
        await using var context = await MigratedAsync();

        var options = Options.Create(new AuditLogOptions { RetentionMonths = 84, PartitionsCreatedAhead = 2 });
        var maintenance = new AuditLogPartitionMaintenance(context, options);

        var outcome = await maintenance.RunAsync();

        // The current month plus the two ahead of it.
        outcome.PartitionsEnsured.Should().HaveCount(3);
        outcome.PartitionsEnsured.Should().Contain($"audit_logs_{TimeProvider.System.GetUtcNow():yyyy_MM}");
        outcome.RetentionMonths.Should().Be(84);
    }

    private static Task<string> CreatePartitionAsync(DbContext context, DateOnly month) =>
        context.Database
            .SqlQuery<string>($"""SELECT platform.create_audit_log_partition({month}) AS "Value" """)
            .SingleAsync();

    private static Task<int> DropExpiredAsync(DbContext context, int retainMonths) =>
        context.Database
            .SqlQuery<int>($"""SELECT platform.drop_expired_audit_log_partitions({retainMonths}) AS "Value" """)
            .SingleAsync();

    private Task<AppDbContext> MigratedAsync([CallerMemberName] string testName = "") =>
        AuditDatabase.MigratedAsync(_postgres, testName);
}
