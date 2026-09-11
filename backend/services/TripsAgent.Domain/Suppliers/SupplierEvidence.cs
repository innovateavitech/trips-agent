using System.Text.Json;
using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Suppliers;

/// <summary>
/// One HTTP call to a supplier, request and response, kept as evidence.
/// </summary>
/// <remarks>
/// <para>
/// This table answers "what did we actually send, and what did they say?" in every dispute, and
/// it powers the supplier error-rate report. It is range-partitioned by month on
/// <see cref="OccurredAt"/> so old months can be dropped whole — see the AddSupplierSchema
/// migration and <c>SupplierApiCallPartitionMaintenance</c>.
/// </para>
/// <para>
/// <b>Credentials and passport numbers cannot reach it.</b> Everything is redacted here, in the
/// factory, rather than trusted to each caller: a bus call carries the merchant key in a
/// <c>MerchantKey</c> header and a hash of it in <c>Authorization</c>, a booking body carries every
/// passenger's passport number, and a table that keeps every request for months is the last place
/// any of them should land. Headers by name (<see cref="IsSensitiveHeader"/>); bodies and the query
/// string through <see cref="SupplierPayloadRedaction"/>.
/// </para>
/// <para>
/// <see cref="AgencyId"/> is nullable — a platform smoke test belongs to no agency — so this is
/// filtered explicitly in <c>AppDbContext</c> rather than through <see cref="ITenantScoped"/>.
/// </para>
/// </remarks>
public sealed class SupplierApiCall : Entity
{
    /// <summary>What a sensitive header's value is replaced with.</summary>
    public const string RedactedValue = "[redacted]";

    /// <summary>
    /// Header-name fragments that mark a credential. Substring, case-insensitive — so
    /// <c>MerchantKey</c>, <c>X-Api-Key</c> and <c>x-auth-token</c> are all caught, including by a
    /// supplier we have not integrated yet.
    /// </summary>
    private static readonly string[] SensitiveHeaderFragments =
    [
        "authorization", "key", "token", "secret", "password", "signature", "cookie", "hash",
    ];

    /// <summary>The column widths, so an overlong value is shortened here rather than failing its insert.</summary>
    public const int EndpointMaxLength = 500;

    public const int ErrorMessageMaxLength = 2000;

    public const int CorrelationIdMaxLength = 100;

    private SupplierApiCall()
    {
        HttpMethod = string.Empty;
        Endpoint = string.Empty;
        RequestHeaders = "{}";
    }

    private SupplierApiCall(Guid id)
        : base(id)
    {
        HttpMethod = string.Empty;
        Endpoint = string.Empty;
        RequestHeaders = "{}";
    }

    /// <summary>
    /// Records a call. Sensitive headers, body fields and query parameters are replaced before
    /// anything is stored.
    /// </summary>
    /// <param name="id">
    /// Chosen by the caller when it has to be known before the row is written — the audit handler
    /// hands it to the adapter so a status poll can point at the call behind it. New when omitted.
    /// </param>
    public static SupplierApiCall Record(
        Guid supplierId,
        Guid? agencyId,
        Guid? supplierBookingId,
        SupplierOperation operation,
        string httpMethod,
        string endpoint,
        IReadOnlyDictionary<string, string> requestHeaders,
        string? requestBody,
        int? responseStatusCode,
        string? responseBody,
        int latencyMs,
        SupplierCallOutcome outcome,
        DateTimeOffset occurredAt,
        string? errorMessage = null,
        string? correlationId = null,
        Guid? id = null)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(supplierId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(httpMethod);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentNullException.ThrowIfNull(requestHeaders);
        ArgumentOutOfRangeException.ThrowIfNegative(latencyMs);
        if (id == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(id), "An empty id would collide with every other empty id.");
        }

        var knownSecrets = SupplierPayloadRedaction.KnownSecretsFrom(requestHeaders);

        return new SupplierApiCall(id ?? Guid.CreateVersion7())
        {
            SupplierId = supplierId,
            AgencyId = agencyId,
            SupplierBookingId = supplierBookingId,
            Operation = operation,
            HttpMethod = httpMethod.ToUpperInvariant(),
            Endpoint = Truncate(SupplierPayloadRedaction.RedactEndpoint(endpoint), EndpointMaxLength)!,
            RequestHeaders = JsonSerializer.Serialize(RedactHeaders(requestHeaders)),
            RequestBody = SupplierPayloadRedaction.RedactRequestBody(requestBody, knownSecrets),
            ResponseStatusCode = responseStatusCode,
            ResponseBody = SupplierPayloadRedaction.RedactResponseBody(responseBody, knownSecrets),
            LatencyMs = latencyMs,
            Outcome = outcome,
            OccurredAt = occurredAt,

            // An exception message can quote a request, so it gets the known-secret pass as well.
            ErrorMessage = Truncate(
                SupplierPayloadRedaction.RedactResponseBody(errorMessage, knownSecrets),
                ErrorMessageMaxLength),
            CorrelationId = Truncate(correlationId, CorrelationIdMaxLength),
        };
    }

    /// <summary>The partition key. Part of the primary key, because PostgreSQL requires it.</summary>
    public DateTimeOffset OccurredAt { get; private set; }

    public Guid? AgencyId { get; private set; }

    public Guid SupplierId { get; private set; }

    public Guid? SupplierBookingId { get; private set; }

    public SupplierOperation Operation { get; private set; }

    public string HttpMethod { get; private set; }

    /// <summary>The path called, relative to the supplier's base URL.</summary>
    public string Endpoint { get; private set; }

    /// <summary>JSON object of header name to value, with every sensitive value redacted.</summary>
    public string RequestHeaders { get; private set; }

    public string? RequestBody { get; private set; }

    /// <summary>Null when no response arrived — a timeout or a dropped connection.</summary>
    public int? ResponseStatusCode { get; private set; }

    /// <summary>
    /// Text, not JSON: a supplier in trouble returns HTML error pages. Redacted, like
    /// <see cref="RequestBody"/>; see <see cref="SupplierPayloadRedaction"/>.
    /// </summary>
    public string? ResponseBody { get; private set; }

    public int LatencyMs { get; private set; }

    public SupplierCallOutcome Outcome { get; private set; }

    public string? ErrorMessage { get; private set; }

    public string? CorrelationId { get; private set; }

    /// <summary>True when a header of this name carries a credential and must never be stored.</summary>
    public static bool IsSensitiveHeader(string headerName) =>
        !string.IsNullOrEmpty(headerName)
        && Array.Exists(
            SensitiveHeaderFragments,
            fragment => headerName.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];

    private static SortedDictionary<string, string> RedactHeaders(IReadOnlyDictionary<string, string> headers)
    {
        var redacted = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in headers)
        {
            redacted[name] = IsSensitiveHeader(name) ? RedactedValue : value;
        }

        return redacted;
    }
}

/// <summary>
/// One status poll of a supplier booking, and what was done about the answer.
/// </summary>
/// <remarks>
/// The evidence trail for any payment reversal: the reversal worker (#43) must never reverse
/// without one of these rows justifying it. Written once, never edited.
/// </remarks>
public sealed class SupplierStatusPoll : Entity, ITenantScoped
{
    private SupplierStatusPoll()
    {
    }

    public static SupplierStatusPoll Record(
        Guid agencyId,
        Guid supplierBookingId,
        DateTimeOffset polledAt,
        SupplierPollOutcome outcome,
        SupplierPollAction actionTaken,
        int? httpStatusCode = null,
        int? supplierStatusCode = null,
        Guid? supplierApiCallId = null,
        string? note = null)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(supplierBookingId, Guid.Empty);

        return new SupplierStatusPoll
        {
            AgencyId = agencyId,
            SupplierBookingId = supplierBookingId,
            PolledAt = polledAt,
            Outcome = outcome,
            ActionTaken = actionTaken,
            HttpStatusCode = httpStatusCode,
            SupplierStatusCode = supplierStatusCode,
            SupplierApiCallId = supplierApiCallId,
            Note = note,
        };
    }

    public Guid AgencyId { get; private set; }

    public Guid SupplierBookingId { get; private set; }

    public DateTimeOffset PolledAt { get; private set; }

    public int? HttpStatusCode { get; private set; }

    /// <summary>The supplier's own status code, verbatim.</summary>
    public int? SupplierStatusCode { get; private set; }

    public SupplierPollOutcome Outcome { get; private set; }

    public SupplierPollAction ActionTaken { get; private set; }

    /// <summary>
    /// The audited call behind this poll. No foreign key: <c>supplier_api_calls</c> is partitioned
    /// and its old months are dropped, while a poll is evidence that has to outlive them.
    /// </summary>
    public Guid? SupplierApiCallId { get; private set; }

    public string? Note { get; private set; }
}
