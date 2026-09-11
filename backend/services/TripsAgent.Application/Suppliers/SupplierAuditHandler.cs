using System.Diagnostics;
using TripsAgent.Domain.Suppliers;

namespace TripsAgent.Application.Suppliers;

/// <summary>
/// Times every supplier call and hands it to <see cref="ISupplierCallRecorder"/> to be kept as
/// evidence in <c>supplier_api_calls</c>. Issue #39.
/// </summary>
/// <remarks>
/// <para>
/// <b>A handler, so no call site can forget it.</b> It sits in the <see cref="HttpClient"/> pipeline
/// that <see cref="SupplierHttpClientRegistration.AddSupplierHttpClient{TClient,TImplementation}"/>
/// builds, so every request an adapter sends passes through it whether the adapter thought about
/// auditing or not.
/// </para>
/// <para>
/// <b>It never retries, and nothing inside it may.</b> One attempt, recorded once, whatever happens.
/// The ticket-issue call is not idempotent (ADR-0003), so a timeout is recorded as
/// <see cref="SupplierCallOutcome.Timeout"/> and surfaced as <see cref="SupplierCallTimeoutException"/> —
/// an unknown outcome to be resolved by polling, not a failure that invites a second attempt. The
/// registration refuses any other handler on a supplier client, so a retry cannot be added later
/// underneath this one.
/// </para>
/// <para>
/// <b>A transport failure is unknown unless it proves otherwise.</b> A connection reset after the
/// request was fully sent — a supplier crash, a load balancer's idle timeout, a deploy — reaches us as
/// the same <see cref="HttpRequestException"/> type as "connection refused", and so does a body cut off
/// after a 200 status line. The supplier may have acted on either. So only a failure whose
/// <see cref="HttpRequestError"/> proves the request never left (the name did not resolve, the
/// connection or the TLS handshake or proxy tunnel was never made) is recorded as
/// <see cref="SupplierCallOutcome.TransportError"/> and rethrown as it is. Everything else is recorded
/// as <see cref="SupplierCallOutcome.OutcomeUnknown"/> and surfaced as
/// <see cref="SupplierCallOutcomeUnknownException"/>, the base of <see cref="SupplierCallTimeoutException"/>,
/// so an adapter that catches the base handles every unknown outcome the same way: by polling.
/// </para>
/// <para>
/// <b>The timeout lives here, not on <see cref="HttpClient.Timeout"/>.</b> HttpClient's timeout
/// cancels the same token as a caller giving up, so from inside the pipeline the two look identical.
/// Owning the timer is what lets a timeout be recorded as one. The registration sets the client's
/// own timeout to infinite so it never fires first.
/// </para>
/// <para>
/// <b>Off the request path.</b> The handler only buffers both bodies — the caller reads the
/// response the same way afterwards — and queues a capture. Redaction and the insert happen on a
/// background writer, so the request pays for neither.
/// </para>
/// <para>
/// <b>Correlation.</b> The adapter passes <see cref="SupplierCallContext.CorrelationId"/> — the
/// request's <c>IAuditContext.CorrelationId</c>, or the checkout saga's order correlation. When it
/// does not, this falls back to <see cref="Activity.Current"/>, exactly as the API's own correlation
/// middleware does when no <c>X-Correlation-Id</c> header was sent.
/// </para>
/// </remarks>
public sealed class SupplierAuditHandler : DelegatingHandler
{
    private readonly ISupplierCallRecorder _recorder;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _timeout;

    public SupplierAuditHandler(ISupplierCallRecorder recorder, TimeProvider clock, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(clock);

        if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "A supplier call timeout must be positive.");
        }

        _recorder = recorder;
        _clock = clock;
        _timeout = timeout;
    }

    /// <summary>How long a call may take before its outcome is recorded as unknown.</summary>
    public TimeSpan CallTimeout => _timeout;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Refused before anything is sent. A call the audit cannot attribute to a supplier would be
        // a row with no supplier — and a call nobody can account for is exactly what this prevents.
        if (!request.Options.TryGetValue(SupplierHttpCall.OptionKey, out var call) || call is null)
        {
            throw new InvalidOperationException(
                $"""
                 A supplier request to {request.RequestUri} was not tagged for auditing, so it was not sent.

                 Every supplier call is recorded in supplier_api_calls. Tag the request before sending it:

                     request.ForSupplierCall(supplierId, SupplierOperation.Search, callContext);
                 """);
        }

        var callId = Guid.CreateVersion7();
        request.Options.Set(SupplierHttpCall.AuditedCallIdKey, callId);

        var occurredAt = _clock.GetUtcNow();
        var headers = CaptureHeaders(request);
        var requestBody = await ReadBodyAsync(request.Content, cancellationToken);
        var correlationId = call.Context.CorrelationId ?? Activity.Current?.Id;

        using var timeoutSource = new CancellationTokenSource(_timeout, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        var started = _clock.GetTimestamp();
        HttpResponseMessage? response = null;

        void Record(SupplierCallOutcome outcome, string? responseBody = null, string? error = null) =>
            _recorder.Record(new SupplierCallCapture
            {
                CallId = callId,
                SupplierId = call.SupplierId,
                AgencyId = call.Context.AgencyId,
                SupplierBookingId = call.Context.SupplierBookingId,
                Operation = call.Operation,
                HttpMethod = request.Method.Method,
                Endpoint = request.RequestUri?.PathAndQuery ?? "/",
                RequestHeaders = headers,
                RequestBody = requestBody,
                ResponseStatusCode = response is null ? null : (int)response.StatusCode,
                ResponseBody = responseBody,
                LatencyMs = (int)Math.Min(_clock.GetElapsedTime(started).TotalMilliseconds, int.MaxValue),
                Outcome = outcome,
                OccurredAt = occurredAt,
                ErrorMessage = error,
                CorrelationId = correlationId,
            });

        try
        {
            response = await base.SendAsync(request, linked.Token);

            // Inside the timer: until the body has arrived, the caller has not had its answer.
            var responseBody = await ReadBodyAsync(response.Content, linked.Token);

            // An error status is still an answer, and the caller decides what it means — so it is
            // recorded and returned, never thrown.
            Record(response.IsSuccessStatusCode ? SupplierCallOutcome.Succeeded : SupplierCallOutcome.HttpError, responseBody);
            return response;
        }
        catch (OperationCanceledException ex) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            Record(SupplierCallOutcome.Timeout, error: $"No response within {_timeout}. Outcome unknown (ADR-0003).");
            response?.Dispose();
            throw new SupplierCallTimeoutException(call.Operation, _timeout, ex);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Record(SupplierCallOutcome.Cancelled, error: "The caller stopped waiting before the supplier answered.");
            response?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            // HttpRequestError is the closest thing a transport failure has to an error code, and the
            // only evidence of whether the request left. Kept in the error message either way.
            var code = ex is HttpRequestException { HttpRequestError: var kind } ? $"{kind}: " : string.Empty;
            var detail = $"{code}{ex.GetType().Name}: {ex.Message}";

            if (response is null && ProvesTheRequestNeverLeft(ex))
            {
                Record(SupplierCallOutcome.TransportError, error: detail);
                throw;
            }

            // Anything else may have happened after the supplier had the whole request: a reset
            // mid-call, a reply that was not HTTP, a body cut off after the status line.
            Record(SupplierCallOutcome.OutcomeUnknown, error: $"{detail} The request may have reached the supplier. Outcome unknown (ADR-0003).");
            response?.Dispose();
            throw new SupplierCallOutcomeUnknownException(call.Operation, ex);
        }
    }

    /// <summary>
    /// True only for the failures that happen before a request is written: resolving the name,
    /// opening the connection, the TLS handshake, the proxy tunnel. Deliberately a short allow list —
    /// a failure not on it is treated as unknown, which costs a status poll, while the opposite
    /// mistake costs a second real ticket.
    /// </summary>
    private static bool ProvesTheRequestNeverLeft(Exception exception) =>
        exception is HttpRequestException
        {
            HttpRequestError: HttpRequestError.NameResolutionError
                or HttpRequestError.ConnectionError
                or HttpRequestError.SecureConnectionError
                or HttpRequestError.ProxyTunnelError,
        };

    /// <summary>
    /// Every header the supplier will see. <see cref="HttpClient"/> has already merged its default
    /// headers into the request by now, so the merchant key set once at registration is here — and
    /// <c>SupplierApiCall.Record</c> redacts it.
    /// </summary>
    private static Dictionary<string, string> CaptureHeaders(HttpRequestMessage request)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, values) in request.Headers)
        {
            headers[name] = string.Join(", ", values);
        }

        if (request.Content is not null)
        {
            foreach (var (name, values) in request.Content.Headers)
            {
                headers[name] = string.Join(", ", values);
            }
        }

        return headers;
    }

    /// <summary>
    /// Buffers a body and reads it as text. Buffering first is what lets the transport — for a
    /// request — or the caller — for a response — read it again afterwards.
    /// </summary>
    private static async Task<string?> ReadBodyAsync(HttpContent? content, CancellationToken cancellationToken)
    {
        if (content is null)
        {
            return null;
        }

        await content.LoadIntoBufferAsync(cancellationToken);
        return await content.ReadAsStringAsync(cancellationToken);
    }
}
