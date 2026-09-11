using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TripsAgent.Application.Suppliers;
using TripsAgent.Domain.Suppliers;

namespace TripsAgent.Infrastructure.Suppliers;

/// <summary>
/// The hand-off between the audit handler, on the request path, and
/// <see cref="SupplierApiCallWriterService"/>, which writes the rows. A singleton.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bounded</b>, at <see cref="SupplierApiCallOptions.QueueCapacity"/>. An unbounded queue behind
/// a database that has stopped answering grows until the process runs out of memory, and then every
/// call in it is lost at once, along with everything else the process was doing.
/// </para>
/// <para>
/// <b>When it is full, the new call is lost — loudly.</b> The alternatives were worse. Waiting for
/// space would hold a request that already has its supplier answer, and if the caller gave up
/// meanwhile, a ticket that <i>was</i> issued would reach the caller as a cancellation. Writing the
/// row inline instead would put the database back on the request path exactly when it is struggling.
/// So the call is written to the log at Error with everything but its bodies — supplier, operation,
/// status, outcome, latency, booking, correlation — which keeps the fact of the call as evidence, and
/// it is counted, both here and on the <c>tripsagent.supplier_api_calls.lost</c> metric.
/// </para>
/// <para>
/// Deliberately <see cref="BoundedChannelFullMode.Wait"/> with <c>TryWrite</c>, not
/// <see cref="BoundedChannelFullMode.DropWrite"/>: with DropWrite, <c>TryWrite</c> reports success
/// for a call it has thrown away, which is the silent loss this class exists to prevent.
/// </para>
/// </remarks>
public sealed partial class SupplierApiCallBuffer : ISupplierCallRecorder
{
    private static readonly Meter Meter = new("TripsAgent.Suppliers");

    private static readonly Counter<long> LostCounter = Meter.CreateCounter<long>(
        "tripsagent.supplier_api_calls.lost",
        unit: "{call}",
        description: "Supplier calls that could not be written to supplier_api_calls.");

    private readonly Channel<SupplierCallCapture> _channel;
    private readonly ILogger<SupplierApiCallBuffer> _logger;
    private long _lost;
    private volatile bool _closed;

    public SupplierApiCallBuffer(IOptions<SupplierApiCallOptions> options, ILogger<SupplierApiCallBuffer> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _channel = Channel.CreateBounded<SupplierCallCapture>(new BoundedChannelOptions(options.Value.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,

            // One writer service reads; any number of concurrent requests write.
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <summary>What the writer service drains.</summary>
    public ChannelReader<SupplierCallCapture> Reader => _channel.Reader;

    /// <summary>Calls that never reached the table since the process started. Each was logged at Error.</summary>
    public long LostCount => Interlocked.Read(ref _lost);

    public void Record(SupplierCallCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);

        if (_channel.Writer.TryWrite(capture))
        {
            return;
        }

        ReportLost(capture, _closed ? "the process is shutting down" : "the audit buffer is full");
    }

    /// <summary>
    /// Stops accepting calls, so the writer can drain what is left knowing nothing more will arrive.
    /// A call made after this is reported lost rather than left in a buffer nobody will read.
    /// </summary>
    public void Close()
    {
        _closed = true;
        _channel.Writer.TryComplete();
    }

    /// <summary>
    /// Logs and counts a call that will not be written. The log line is then the only evidence the
    /// call happened, so it carries everything except the bodies — which could hold a passport number.
    /// </summary>
    public void ReportLost(SupplierCallCapture capture, string reason, Exception? exception = null)
    {
        ArgumentNullException.ThrowIfNull(capture);

        var total = Interlocked.Increment(ref _lost);
        LostCounter.Add(1, new KeyValuePair<string, object?>("operation", capture.Operation.ToString()));

        LogLost(
            _logger,
            exception,
            reason,
            capture.CallId,
            capture.Operation,
            capture.HttpMethod,
            SupplierPayloadRedaction.RedactEndpoint(capture.Endpoint),
            capture.SupplierId,
            capture.AgencyId,
            capture.SupplierBookingId,
            capture.ResponseStatusCode,
            capture.Outcome,
            capture.LatencyMs,
            capture.OccurredAt,
            capture.CorrelationId,
            total);
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Supplier call NOT recorded in supplier_api_calls because {Reason}. This line is the only "
                  + "evidence of it: call {CallId}, {Operation} {HttpMethod} {Endpoint} to supplier {SupplierId} "
                  + "for agency {AgencyId}, booking {SupplierBookingId}; status {StatusCode}, outcome {Outcome}, "
                  + "{LatencyMs} ms, at {OccurredAt}, correlation {CorrelationId}. {LostTotal} lost since start.")]
    private static partial void LogLost(
        ILogger logger,
        Exception? exception,
        string reason,
        Guid callId,
        SupplierOperation operation,
        string httpMethod,
        string endpoint,
        Guid supplierId,
        Guid? agencyId,
        Guid? supplierBookingId,
        int? statusCode,
        SupplierCallOutcome outcome,
        int latencyMs,
        DateTimeOffset occurredAt,
        string? correlationId,
        long lostTotal);
}
