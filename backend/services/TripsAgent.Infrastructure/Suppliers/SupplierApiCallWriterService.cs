using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TripsAgent.Application.Suppliers;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Suppliers;
using TripsAgent.Infrastructure.Persistence;

namespace TripsAgent.Infrastructure.Suppliers;

/// <summary>
/// Drains <see cref="SupplierApiCallBuffer"/> into <c>supplier_api_calls</c>, a batch at a time.
/// Runs in both the Api and the Worker, because both call suppliers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Each row is written as its own agency.</b> A batch holds many agencies' calls, and row-level
/// security admits a row only for the agency the connection is acting for (ADR-0006). The batch is
/// split by agency and each part saved in a scope acting as that agency, so the policy's WITH CHECK
/// proves every row belongs where it is written. Only calls made for no agency — platform work such
/// as a smoke test — go through <see cref="IPlatformScope"/>. Entering the platform scope for every
/// batch instead would log a cross-tenant warning every few hundred milliseconds and prove nothing.
/// </para>
/// <para>
/// <b>Every call is either written or reported.</b> If a batch fails, its rows are retried one at a
/// time, so whatever was wrong is confined to the row that caused it. A row that still cannot be
/// written — or that shutdown overtakes — is reported lost by the buffer: logged at Error with
/// everything but its bodies, and counted. Nothing leaves this class unaccounted for.
/// </para>
/// <para>
/// <b>Shutdown drains, on one clock.</b> When the host stops, the batch in hand is finished, the
/// buffer is closed and what is left is written — all within
/// <see cref="SupplierApiCallOptions.ShutdownDrainTimeout"/> of <see cref="StopAsync"/> being called.
/// Past that, every save is cancelled, so each call still held is reported rather than written. It
/// never throws out of <see cref="ExecuteAsync"/>: the Worker stops the whole host when a background
/// service fails.
/// </para>
/// <para>
/// <b><see cref="StopAsync"/> does not return while a call is still in memory.</b> The host hands it
/// a token that may already have expired — in the Api the web server stops first, from the same
/// shutdown budget, and a slow supplier request can spend all of it — and
/// <see cref="BackgroundService.StopAsync"/> stops waiting for <see cref="ExecuteAsync"/> when that
/// token does. Returning then would let the host dispose the container, and then the process exit,
/// with calls unwritten and unreported. So this waits for <see cref="ExecuteAsync"/> itself; the
/// drain clock is what bounds the wait.
/// </para>
/// <para>
/// The drain runs once, as one shared task, awaited by both <see cref="ExecuteAsync"/> and
/// <see cref="StopAsync"/>. <see cref="BackgroundService"/> starts <c>ExecuteAsync</c> on the thread
/// pool, and a host stopped moments after starting can cancel it before it ever runs — which would
/// otherwise leave whatever was buffered unwritten and unreported.
/// </para>
/// <para>
/// The drain clock starts <i>after</i> the host's other services have used their share, so a deployment
/// platform's grace period must cover the host's <c>ShutdownTimeout</c> plus
/// <see cref="SupplierApiCallOptions.ShutdownDrainTimeout"/>, or it kills the process first.
/// </para>
/// </remarks>
public sealed partial class SupplierApiCallWriterService(
    SupplierApiCallBuffer buffer,
    IServiceScopeFactory scopeFactory,
    IOptions<SupplierApiCallOptions> options,
    ILogger<SupplierApiCallWriterService> logger) : BackgroundService
{
    private const string PlatformScopeReason =
        "Recording supplier calls made for no agency (platform work, such as a smoke test)";

    private const string StoppedReason = "the process stopped before it could be written";

    /// <summary>Cancelled <see cref="SupplierApiCallOptions.ShutdownDrainTimeout"/> after stopping begins.</summary>
    private readonly CancellationTokenSource _shutdownDeadline = new();

    private readonly Lock _drainLock = new();
    private Task? _drain;
    private int _stopping;

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _stopping, 1) == 0)
        {
            _shutdownDeadline.CancelAfter(options.Value.ShutdownDrainTimeout);
        }

        // Signals ExecuteAsync, and waits for it only while the host's token allows.
        await base.StopAsync(cancellationToken);

        // So wait for it regardless. It finishes soon after the deadline: every save it makes is
        // cancelled then, and each call it still holds is reported.
        if (ExecuteTask is { } execute)
        {
            await execute.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        // Normally already done by ExecuteAsync; this runs it if ExecuteAsync never started.
        await DrainOnceAsync();
    }

    public override void Dispose()
    {
        _shutdownDeadline.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batchSize = options.Value.BatchSize;
        var batch = new List<SupplierCallCapture>(batchSize);

        try
        {
            while (await buffer.Reader.WaitToReadAsync(stoppingToken))
            {
                Fill(batch, batchSize);

                // Not stoppingToken: a batch already taken from the buffer is finished rather than
                // abandoned half-written — but only until the shutdown deadline. Retries against a
                // database that has stopped answering could otherwise outlast the process, and the
                // batch would vanish with it instead of being reported.
                await WriteAsync(batch, _shutdownDeadline.Token);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is stopping. Fall through to the drain.
        }

        await DrainOnceAsync();
    }

    /// <summary>Starts the drain the first time, and returns that same task every time after.</summary>
    private Task DrainOnceAsync()
    {
        lock (_drainLock)
        {
            return _drain ??= Task.Run(DrainAsync);
        }
    }

    private async Task DrainAsync()
    {
        buffer.Close();

        var batchSize = options.Value.BatchSize;
        var batch = new List<SupplierCallCapture>(batchSize);

        // Once the deadline passes, WriteAsync reports each remaining row instead of writing it, so
        // this loop still empties the buffer — every call left gets its log line.
        while (buffer.Reader.TryPeek(out _))
        {
            Fill(batch, batchSize);
            await WriteAsync(batch, _shutdownDeadline.Token);
        }
    }

    private void Fill(List<SupplierCallCapture> batch, int batchSize)
    {
        batch.Clear();

        while (batch.Count < batchSize && buffer.Reader.TryRead(out var capture))
        {
            batch.Add(capture);
        }
    }

    /// <summary>Writes a batch. Never throws: each call is written or reported lost.</summary>
    private async Task WriteAsync(List<SupplierCallCapture> batch, CancellationToken cancellationToken)
    {
        // Redaction happens here, off the request path. A capture Record refuses is a bug in the
        // handler; it is lost alone, not with its batch.
        var rows = new List<(SupplierCallCapture Capture, SupplierApiCall Call)>(batch.Count);

        foreach (var capture in batch)
        {
            try
            {
                rows.Add((capture, capture.ToApiCall()));
            }
            catch (Exception ex)
            {
                buffer.ReportLost(capture, "it was not a valid call record", ex);
            }
        }

        foreach (var agency in rows.GroupBy(row => row.Call.AgencyId))
        {
            var group = agency.ToList();

            try
            {
                await SaveAsync(agency.Key, group.Select(row => row.Call), cancellationToken);
            }
            catch (Exception ex) when (group.Count > 1 && !cancellationToken.IsCancellationRequested)
            {
                LogBatchFailed(logger, ex, group.Count, agency.Key);
                await SaveOneByOneAsync(agency.Key, group, cancellationToken);
            }
            catch (Exception ex)
            {
                ReportAll(group, ex, cancellationToken);
            }
        }
    }

    private async Task SaveOneByOneAsync(
        Guid? agencyId,
        List<(SupplierCallCapture Capture, SupplierApiCall Call)> rows,
        CancellationToken cancellationToken)
    {
        foreach (var row in rows)
        {
            try
            {
                await SaveAsync(agencyId, [row.Call], cancellationToken);
            }
            catch (Exception ex)
            {
                ReportAll([row], ex, cancellationToken);
            }
        }
    }

    private void ReportAll(
        List<(SupplierCallCapture Capture, SupplierApiCall Call)> rows,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var reason = cancellationToken.IsCancellationRequested ? StoppedReason : "the database refused it";

        foreach (var row in rows)
        {
            buffer.ReportLost(row.Capture, reason, exception);
        }
    }

    private async Task SaveAsync(Guid? agencyId, IEnumerable<SupplierApiCall> calls, CancellationToken cancellationToken)
    {
        // A fresh scope, and so a fresh context and tenant, for every save: a context that failed is
        // not reused, and a TenantContext can be set only once.
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;

        IDisposable? platformScope = null;

        if (agencyId is { } id)
        {
            services.GetRequiredService<TenantContext>().SetTenant(id);
        }
        else
        {
            platformScope = services.GetRequiredService<IPlatformScope>().Enter(PlatformScopeReason);
        }

        using (platformScope)
        {
            var db = services.GetRequiredService<AppDbContext>();
            db.SupplierApiCalls.AddRange(calls);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Writing a batch of {Count} supplier calls for agency {AgencyId} failed; retrying them one "
                  + "at a time so the row at fault is the only one lost.")]
    private static partial void LogBatchFailed(ILogger logger, Exception exception, int count, Guid? agencyId);
}
