using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TripsAgent.Application.Analytics;
using TripsAgent.Application.Storage;
using TripsAgent.Domain.Analytics;
using TripsAgent.Domain.Auditing;
using TripsAgent.Domain.Identity;
using TripsAgent.Infrastructure.Auditing;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Analytics;

/// <summary>
/// Running reports: what comes back in the request, what goes to the background, and what is
/// written down about it either way.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ReportingTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 10, 9, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _postgres;

    public ReportingTests(PostgresFixture postgres) => _postgres = postgres;

    // ------------------------------------------------------------------------ sync and async

    [Fact]
    public async Task A_short_agency_report_comes_back_in_the_request()
    {
        await using var world = await AnalyticsWorld.CreateAsync(_postgres, Now, "reports_sync");
        var agency = await world.AddAgencyAsync("Lagos Travel Limited", "lagos-travel");
        await world.PlaceOrderAsync(agency, "ORD-1", Now);
        await world.Rollup.RunFullRebuildAsync();

        var harness = world.Reporting(agency);

        var result = await harness.Reports.RequestAsync(
            ReportCatalog.AgencySalesDaily,
            LagosDay.Of(Now).AddDays(-7),
            LagosDay.Of(Now),
            [PermissionCodes.ReportView, PermissionCodes.MarginView],
            showMargin: true);

        result.Outcome.Should().Be(ReportRequestOutcome.Accepted);
        result.Content.Should().NotBeNull();
        result.Job!.RunMode.Should().Be(ReportRunMode.Synchronous);
        result.Job.Status.Should().Be(ReportJobStatus.Succeeded);
        result.Job.RowCount.Should().Be(1);

        var text = Encoding.UTF8.GetString(result.Content!.Content);
        text.Should().Contain("Gross sales (NGN)").And.Contain("1107.50");

        // margin.view was held, so the cost columns are there.
        text.Should().Contain("Margin (NGN)");
    }

    [Fact]
    public async Task A_report_over_ninety_days_is_queued_instead()
    {
        await using var world = await AnalyticsWorld.CreateAsync(_postgres, Now, "reports_long");
        var agency = await world.AddAgencyAsync("Lagos Travel Limited", "lagos-travel");
        var harness = world.Reporting(agency);

        var result = await harness.Reports.RequestAsync(
            ReportCatalog.AgencySalesDaily,
            LagosDay.Of(Now).AddDays(-120),
            LagosDay.Of(Now),
            [PermissionCodes.ReportView],
            showMargin: false);

        result.Outcome.Should().Be(ReportRequestOutcome.Accepted);
        result.Content.Should().BeNull("nobody waits four months of rows out on an HTTP request");
        result.Job!.RunMode.Should().Be(ReportRunMode.Asynchronous);
        result.Job.Status.Should().Be(ReportJobStatus.Queued);

        harness.Dispatcher.Enqueued.Should().ContainSingle().Which.Should().Be(result.Job.Id);
    }

    [Fact]
    public async Task A_platform_report_is_queued_however_short_its_window()
    {
        await using var world = await AnalyticsWorld.CreateAsync(_postgres, Now, "reports_platform");
        await world.AddAgencyAsync("Lagos Travel Limited", "lagos-travel");
        var harness = world.Reporting(agencyId: null);

        var result = await harness.Reports.RequestAsync(
            ReportCatalog.PlatformGmvDaily,
            LagosDay.Of(Now),
            LagosDay.Of(Now),
            [PermissionCodes.PlatformReportView],
            showMargin: true);

        result.Outcome.Should().Be(ReportRequestOutcome.Accepted);
        result.Job!.RunMode.Should().Be(ReportRunMode.Asynchronous);
        result.Job.AgencyId.Should().BeNull("a platform report belongs to no agency");
    }

    [Fact]
    public async Task A_report_the_caller_may_not_run_is_refused()
    {
        await using var world = await AnalyticsWorld.CreateAsync(_postgres, Now, "reports_forbidden");
        var agency = await world.AddAgencyAsync("Lagos Travel Limited", "lagos-travel");
        var harness = world.Reporting(agency);

        var result = await harness.Reports.RequestAsync(
            ReportCatalog.PlatformGmvDaily,
            LagosDay.Of(Now),
            LagosDay.Of(Now),
            [PermissionCodes.ReportView],
            showMargin: false);

        result.Outcome.Should().Be(ReportRequestOutcome.Forbidden);
        result.Job.Should().BeNull("a refused report is not a run");
    }

    [Fact]
    public async Task Margin_columns_are_absent_without_the_permission()
    {
        await using var world = await AnalyticsWorld.CreateAsync(_postgres, Now, "reports_margin");
        var agency = await world.AddAgencyAsync("Lagos Travel Limited", "lagos-travel");
        await world.PlaceOrderAsync(agency, "ORD-1", Now);
        await world.Rollup.RunFullRebuildAsync();

        var harness = world.Reporting(agency);

        var result = await harness.Reports.RequestAsync(
            ReportCatalog.AgencySalesDaily,
            LagosDay.Of(Now),
            LagosDay.Of(Now),
            [PermissionCodes.ReportView],
            showMargin: false);

        var text = Encoding.UTF8.GetString(result.Content!.Content);

        text.Should().Contain("Gross sales (NGN)");
        text.Should().NotContain("Margin").And.NotContain("Cost (NGN)");
    }

    // ------------------------------------------------------------ one person's file is their own

    [Fact]
    public async Task A_colleague_cannot_list_or_download_somebody_elses_report()
    {
        await using var world = await AnalyticsWorld.CreateAsync(_postgres, Now, "reports_mine");
        var agency = await world.AddAgencyAsync("Lagos Travel Limited", "lagos-travel");
        await world.PlaceOrderAsync(agency, "ORD-1", Now);
        await world.Rollup.RunFullRebuildAsync();

        var owner = await world.AddUserAsync(agency, "ada@lagos-travel.test", "Ada");
        var colleague = await world.AddUserAsync(agency, "emeka@lagos-travel.test", "Emeka");

        // The owner runs a report with margin in it, which is the whole point of the attack: net
        // rate and markup sitting in a file, addressable by id.
        var mine = world.Reporting(agency, owner);

        var result = await mine.Reports.RequestAsync(
            ReportCatalog.AgencySalesDaily,
            LagosDay.Of(Now).AddDays(-7),
            LagosDay.Of(Now),
            [PermissionCodes.ReportView, PermissionCodes.MarginView],
            showMargin: true);

        var jobId = result.Job!.Id;

        (await mine.Reports.JobsAsync()).Should().ContainSingle().Which.Id.Should().Be(jobId);
        (await mine.Reports.DownloadAsync(jobId)).Should().NotBeNull("it is their own report");

        // Somebody else in the same agency, with no margin.view of their own, knows the id.
        var theirs = world.Reporting(agency, colleague);

        (await theirs.Reports.JobsAsync()).Should().BeEmpty("a report belongs to whoever asked for it");
        (await theirs.Reports.JobAsync(jobId)).Should().BeNull();
        (await theirs.Reports.DownloadAsync(jobId)).Should().BeNull(
            "a finished report can carry net rate and markup — issue 110");
    }

    // --------------------------------------------------------------------- every export is logged

    [Fact]
    public async Task Producing_a_file_is_logged_with_actor_scope_rows_and_time()
    {
        await using var world = await AnalyticsWorld.CreateAsync(_postgres, Now, "reports_audit");
        var agency = await world.AddAgencyAsync("Lagos Travel Limited", "lagos-travel");
        await world.PlaceOrderAsync(agency, "ORD-1", Now);
        await world.Rollup.RunFullRebuildAsync();

        var actor = await world.AddUserAsync(agency, "ada@lagos-travel.test", "Ada");
        var harness = world.Reporting(agency, actor);

        await harness.Reports.RequestAsync(
            ReportCatalog.AgencySalesDaily,
            LagosDay.Of(Now),
            LagosDay.Of(Now),
            [PermissionCodes.ReportView],
            showMargin: false);

        await using var platform = world.AsPlatform();
        var entry = await platform.ReportExportAudits.SingleAsync();

        entry.ActorUserId.Should().Be(actor);
        entry.ActorType.Should().Be(AuditActorType.User);
        entry.ActorIpAddress.Should().Be("198.51.100.7");
        entry.AgencyId.Should().Be(agency);
        entry.Scope.Should().Be(ReportScope.Agency);
        entry.RowCount.Should().Be(1);
        entry.ExportedAt.Should().Be(Now);
        entry.ScopeDescription.Should().Contain("Lagos Travel Limited").And.Contain("generated");
    }

    [Fact]
    public async Task Downloading_a_file_again_is_logged_again()
    {
        await using var world = await AnalyticsWorld.CreateAsync(_postgres, Now, "reports_download");
        var agency = await world.AddAgencyAsync("Lagos Travel Limited", "lagos-travel");
        await world.PlaceOrderAsync(agency, "ORD-1", Now);
        await world.Rollup.RunFullRebuildAsync();

        // Signed in as somebody: a report belongs to whoever asked for it, and only they can take
        // the file (issue 110).
        var harness = world.Reporting(agency, await world.AddUserAsync(agency, "ada@lagos-travel.test", "Ada"));

        var result = await harness.Reports.RequestAsync(
            ReportCatalog.AgencySalesDaily,
            LagosDay.Of(Now),
            LagosDay.Of(Now),
            [PermissionCodes.ReportView],
            showMargin: false);

        var download = await harness.Reports.DownloadAsync(result.Job!.Id);

        download.Should().NotBeNull();
        download!.FileName.Should().Be("agency.sales.daily_2026-03-10_2026-03-10.csv");
        download.Content.Should().Equal(result.Content!.Content);

        await using var platform = world.AsPlatform();
        var entries = await platform.ReportExportAudits.OrderBy(entry => entry.ExportedAt).ToListAsync();

        // A file taken twice has left twice, and the log says which was which.
        entries.Should().HaveCount(2);
        entries[0].ScopeDescription.Should().EndWith("generated");
        entries[1].ScopeDescription.Should().EndWith("downloaded");
    }

    [Fact]
    public async Task A_failed_report_exports_nothing_and_logs_nothing()
    {
        await using var world = await AnalyticsWorld.CreateAsync(_postgres, Now, "reports_failed");
        var agency = await world.AddAgencyAsync("Lagos Travel Limited", "lagos-travel");
        var harness = world.Reporting(agency, storage: new FailingBlobStorage());

        var act = () => harness.Reports.RequestAsync(
            ReportCatalog.AgencySalesDaily,
            LagosDay.Of(Now),
            LagosDay.Of(Now),
            [PermissionCodes.ReportView],
            showMargin: false);

        await act.Should().ThrowAsync<IOException>();

        await using var platform = world.AsPlatform();
        (await platform.ReportExportAudits.CountAsync()).Should().Be(0);
    }

    // -------------------------------------------------------------------------- the worker's path

    [Fact]
    public async Task A_queued_agency_report_runs_under_its_own_agency()
    {
        await using var world = await AnalyticsWorld.CreateAsync(_postgres, Now, "reports_worker");
        var lagos = await world.AddAgencyAsync("Lagos Travel Limited", "lagos-travel");
        var kano = await world.AddAgencyAsync("Kano Journeys", "kano-journeys");

        await world.PlaceOrderAsync(lagos, "ORD-1", Now);
        await world.PlaceOrderAsync(kano, "ORD-2", Now, netMinor: 900_000, markupMinor: 90_000);
        await world.Rollup.RunFullRebuildAsync();

        var requested = world.Reporting(lagos);

        var queued = await requested.Reports.RequestAsync(
            ReportCatalog.AgencyBookings,
            LagosDay.Of(Now).AddDays(-200),
            LagosDay.Of(Now),
            [PermissionCodes.ReportView],
            showMargin: false);

        queued.Job!.RunMode.Should().Be(ReportRunMode.Asynchronous);

        // A fresh scope with no tenant at all — exactly what Hangfire hands a job.
        await world.RunQueuedReportAsync(queued.Job.Id);

        await using var platform = world.AsPlatform();
        var finished = await platform.ReportJobs.SingleAsync(job => job.Id == queued.Job.Id);

        finished.Status.Should().Be(ReportJobStatus.Succeeded);
        finished.RowCount.Should().Be(1, "Lagos sold one booking; Kano's is not Lagos's business");
    }

    [Fact]
    public async Task Finishing_a_queued_report_tells_whoever_asked_for_it()
    {
        await using var world = await AnalyticsWorld.CreateAsync(_postgres, Now, "reports_notify");
        var agency = await world.AddAgencyAsync("Lagos Travel Limited", "lagos-travel");
        var user = await world.AddUserAsync(agency, "ada@lagos-travel.test", "Ada");
        await world.PlaceOrderAsync(agency, "ORD-1", Now);
        await world.Rollup.RunFullRebuildAsync();

        var harness = world.Reporting(agency, user);

        var queued = await harness.Reports.RequestAsync(
            ReportCatalog.AgencySalesDaily,
            LagosDay.Of(Now).AddDays(-200),
            LagosDay.Of(Now),
            [PermissionCodes.ReportView],
            showMargin: false);

        await world.RunQueuedReportAsync(queued.Job!.Id);

        await using var platform = world.AsPlatform();
        var notification = await platform.Notifications.SingleAsync();

        notification.TemplateKey.Should().Be("reports.ready");
        notification.RecipientAddress.Should().Be("ada@lagos-travel.test");
        notification.AgencyId.Should().Be(agency);
    }

    /// <summary>Storage that refuses to write, so a failure can be asserted rather than imagined.</summary>
    private sealed class FailingBlobStorage : IBlobStorage
    {
        public Task<StoredBlob> StoreAsync(Stream content, string key, string contentType, CancellationToken cancellationToken = default) =>
            throw new IOException("The bucket is not reachable.");

        public Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default) =>
            throw new IOException("The bucket is not reachable.");

        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<long?> GetSizeAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<long?>(null);

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<PresignedUpload> CreateUploadUrlAsync(string key, string contentType, long maxSizeBytes, DateTimeOffset expiresAt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SignedDownload> CreateDownloadUrlAsync(string key, string contentType, DateTimeOffset expiresAt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
