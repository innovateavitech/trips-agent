using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TripsAgent.Application.Suppliers;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Suppliers;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.Infrastructure.Suppliers;
using TripsAgent.Infrastructure.Tenancy;

namespace TripsAgent.UnitTests.Suppliers;

/// <summary>
/// The writer's shutdown, over a database that never answers. Issue #39: when the host has finished
/// stopping the writer, every call is either in the table or reported lost — never still in memory
/// in a process about to exit.
/// </summary>
public class SupplierApiCallWriterServiceTests
{
    private readonly ListLogger<SupplierApiCallBuffer> _logger = new();

    [Fact]
    public async Task Stopping_with_no_time_left_still_reports_every_call_before_it_returns()
    {
        // The Api's shape of the problem: the web server spent the host's whole shutdown budget on a
        // slow request, so the writer is asked to stop with a token that has already expired — while
        // it holds a batch the database is not answering.
        var database = new HangingDatabase();
        var options = new SupplierApiCallOptions { BatchSize = 1, ShutdownDrainTimeout = TimeSpan.FromMilliseconds(300) };
        await using var services = Services(options, database);

        var buffer = services.GetRequiredService<SupplierApiCallBuffer>();
        var writer = services.GetRequiredService<SupplierApiCallWriterService>();

        await writer.StartAsync(CancellationToken.None);
        var captures = Enumerable.Range(0, 3).Select(_ => Capture()).ToList();
        captures.ForEach(buffer.Record);

        await database.Entered.WaitAsync(TimeSpan.FromSeconds(30));

        await writer.StopAsync(new CancellationToken(canceled: true)).WaitAsync(TimeSpan.FromSeconds(30));

        writer.ExecuteTask!.IsCompleted.Should().BeTrue("nothing may still be writing once the host moves on to dispose");
        buffer.LostCount.Should().Be(3, "the batch in hand and the two still queued are each reported, not abandoned");

        var reported = _logger.Lines.Where(line => line.Level == LogLevel.Error).Select(line => line.Message).ToList();
        foreach (var capture in captures)
        {
            reported.Should().ContainSingle(message => message.Contains(capture.CallId.ToString()))
                .Which.Should().Contain("the process stopped before it could be written");
        }
    }

    private ServiceProvider Services(SupplierApiCallOptions options, HangingDatabase database)
    {
        var services = new ServiceCollection();

        services.AddSingleton(Options.Create(options));
        services.AddSingleton<ILogger<SupplierApiCallBuffer>>(_logger);
        services.AddSingleton<ILogger<SupplierApiCallWriterService>>(NullLogger<SupplierApiCallWriterService>.Instance);
        services.AddSingleton<SupplierApiCallBuffer>();
        services.AddSingleton<SupplierApiCallWriterService>();

        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<IPlatformScope>(sp => new PlatformScope(
            sp.GetRequiredService<ITenantContext>(), NullLogger<PlatformScope>.Instance));

        // A connection string that is never opened: the interceptor holds every save before it gets there.
        services.AddScoped(sp => new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql("Host=never-opened.invalid;Database=none")
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(database)
                .Options,
            TimeProvider.System,
            sp.GetRequiredService<ITenantContext>(),
            sp.GetRequiredService<IPlatformScope>()));

        return services.BuildServiceProvider();
    }

    private static SupplierCallCapture Capture() => new()
    {
        CallId = Guid.CreateVersion7(),
        SupplierId = Guid.CreateVersion7(),
        AgencyId = Guid.CreateVersion7(),
        Operation = SupplierOperation.Issue,
        HttpMethod = "POST",
        Endpoint = "/api/v2/ticketing/issue",
        RequestHeaders = new Dictionary<string, string>(),
        RequestBody = "{}",
        ResponseStatusCode = 200,
        ResponseBody = """{"StatusCode":1}""",
        LatencyMs = 812,
        Outcome = SupplierCallOutcome.Succeeded,
        OccurredAt = DateTimeOffset.UtcNow,
    };

    /// <summary>A database that has stopped answering: every save waits until it is cancelled.</summary>
    private sealed class HangingDatabase : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once the writer is holding a batch it cannot save.</summary>
        public Task Entered => _entered.Task;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return result;
        }
    }
}
