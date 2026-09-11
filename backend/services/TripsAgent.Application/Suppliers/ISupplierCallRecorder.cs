using TripsAgent.Domain.Suppliers;

namespace TripsAgent.Application.Suppliers;

/// <summary>
/// Accepts a finished supplier call to be written to <c>supplier_api_calls</c>, off the request path.
/// </summary>
/// <remarks>
/// Implemented in Infrastructure by a bounded in-memory queue that a background service drains in
/// batches. The audit handler calls this after every supplier response, so it must cost the request
/// next to nothing: it never blocks, never touches the database, and never throws. A call it cannot
/// accept is logged loudly and counted by the implementation — never silently dropped.
/// </remarks>
public interface ISupplierCallRecorder
{
    public void Record(SupplierCallCapture capture);
}

/// <summary>
/// A supplier call exactly as it happened — <b>unredacted</b>, and never stored or logged in this form.
/// </summary>
/// <remarks>
/// <para>
/// Redaction happens in <see cref="ToApiCall"/>, on the background writer, rather than in the
/// handler: parsing a large search response to redact it would otherwise add to the latency of
/// every search an agent runs. The raw values live only in memory, for as long as the queue holds
/// them, and they were already in memory in the request and response this was copied from.
/// </para>
/// <para>
/// A class rather than a record, deliberately: a record's generated <c>ToString</c> prints every
/// property, and one careless log line would then write a merchant key and a passport number.
/// </para>
/// </remarks>
public sealed class SupplierCallCapture
{
    /// <summary>The id the stored row will have — known now, so the adapter can refer to it.</summary>
    public required Guid CallId { get; init; }

    public required Guid SupplierId { get; init; }

    public Guid? AgencyId { get; init; }

    public Guid? SupplierBookingId { get; init; }

    public required SupplierOperation Operation { get; init; }

    public required string HttpMethod { get; init; }

    public required string Endpoint { get; init; }

    public required IReadOnlyDictionary<string, string> RequestHeaders { get; init; }

    public string? RequestBody { get; init; }

    public int? ResponseStatusCode { get; init; }

    public string? ResponseBody { get; init; }

    public required int LatencyMs { get; init; }

    public required SupplierCallOutcome Outcome { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public string? ErrorMessage { get; init; }

    public string? CorrelationId { get; init; }

    /// <summary>The row to store, with every credential and document number redacted.</summary>
    public SupplierApiCall ToApiCall() =>
        SupplierApiCall.Record(
            SupplierId,
            AgencyId,
            SupplierBookingId,
            Operation,
            HttpMethod,
            Endpoint,
            RequestHeaders,
            RequestBody,
            ResponseStatusCode,
            ResponseBody,
            LatencyMs,
            Outcome,
            OccurredAt,
            ErrorMessage,
            CorrelationId,
            CallId);
}
