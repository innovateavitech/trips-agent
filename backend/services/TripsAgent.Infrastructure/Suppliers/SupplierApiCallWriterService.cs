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
/// <b>Shutdown drains.</b> When the host stops, the buffer is closed and what is left is written,
/// for up to <see cref="SupplierApiCallOptions.ShutdownDrainTimeout"/>. It never throws out of
/// <see cref="ExecuteAsync"/>: the Worker stops the whole host when a background service fails.
/// </para>
/// <para>
/// The drain runs from <see cref="StopAsync"/> as well as from the end of <see cref="ExecuteAsync"/>,
/// once, whichever gets there first. <see cref="BackgroundService"/> starts <c>ExecuteAsync</c> on the
/// thread pool, and a host stopped moments after starting can cancel it before it ever runs — which
/// would otherwise leave whatever was buffered unwritten and unreported.
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

    private int _drainStarted;

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Waits for ExecuteAsync, which normally drains; this catches the case where it never ran.
        await base.StopAsync(cancellationToken);
        await DrainAsync();
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
                // abandoned half-written. The database's own command timeout still bounds it.
                await WriteAsync(batch, CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is stopping. Fall through to the drain.
        }

        await DrainAsync();
    }

    private async Task DrainAsync()
    {
        if (Interlocked.Exchange(ref _drainStarted, 1) == 1)
        {
            return;
        }

        buffer.Close();

        var batchSize = options.Value.BatchSize;
        var batch = new List<SupplierCallCapture>(batchSize);

        using var deadline = new CancellationTokenSource(options.Value.ShutdownDrainTimeout);

        // Once the deadline passes, WriteAsync reports each remaining row instead of writing it, so
        // this loop still empties the buffer — every call left gets its log line.
        while (buffer.Reader.TryPeek(out _))
        {
            Fill(batch, batchSize);
            await WriteAsync(batch, deadline.Token);
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
