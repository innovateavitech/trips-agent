using System.Diagnostics;
using System.Net;
using System.Text;
using FluentAssertions;
using TripsAgent.Application.Suppliers;
using TripsAgent.Domain.Suppliers;

namespace TripsAgent.UnitTests.Suppliers;

/// <summary>Keeps every capture the audit handler hands over.</summary>
internal sealed class RecordingSupplierCallRecorder : ISupplierCallRecorder
{
    public List<SupplierCallCapture> Captures { get; } = [];

    public void Record(SupplierCallCapture capture) => Captures.Add(capture);
}

/// <summary>Stands in for the network: answers however the test says, and counts every attempt.</summary>
internal sealed class StubSupplierHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    : HttpMessageHandler
{
    private int _attempts;

    public int Attempts => _attempts;

    public static StubSupplierHandler Answering(HttpStatusCode status, string body) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        }));

    /// <summary>A supplier that has gone quiet: never answers, only notices being cancelled.</summary>
    public static StubSupplierHandler NeverAnswering() =>
        new(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new UnreachableException();
        });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _attempts);
        return respond(request, cancellationToken);
    }
}

/// <summary>
/// The audit handler on its own, over a stub network. Issue #39: every call recorded with its
/// status, outcome, latency and correlation id — and never retried (ADR-0003).
/// </summary>
public class SupplierAuditHandlerTests
{
    private static readonly Guid SupplierId = Guid.CreateVersion7();
    private static readonly Guid AgencyId = Guid.CreateVersion7();
    private static readonly Guid BookingId = Guid.CreateVersion7();

    private readonly RecordingSupplierCallRecorder _recorder = new();

    [Fact]
    public async Task A_successful_call_is_recorded_and_the_caller_still_reads_the_response()
    {
        var stub = new StubSupplierHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(40, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"StatusCode":1}""") };
        });
        using var client = ClientOver(stub);
        using var request = IssueRequest("""{"DocNumber":"A01234567"}""");

        using var response = await client.SendAsync(request);

        (await response.Content.ReadAsStringAsync()).Should().Be("""{"StatusCode":1}""", "auditing must not consume the body");

        var capture = _recorder.Captures.Should().ContainSingle().Subject;
        capture.Outcome.Should().Be(SupplierCallOutcome.Succeeded);
        capture.ResponseStatusCode.Should().Be(200);
        capture.LatencyMs.Should().BeGreaterThanOrEqualTo(30);
        capture.HttpMethod.Should().Be("POST");
        capture.Endpoint.Should().Be("/api/v2/ticketing/issue?channel=web");
        capture.SupplierId.Should().Be(SupplierId);
        capture.AgencyId.Should().Be(AgencyId);
        capture.SupplierBookingId.Should().Be(BookingId);
        capture.Operation.Should().Be(SupplierOperation.Issue);
        capture.CorrelationId.Should().Be("order-7f3a");
        capture.ResponseBody.Should().Be("""{"StatusCode":1}""");

        // The adapter can point a status poll at the row before the row is even written.
        request.AuditedCallId().Should().Be(capture.CallId);

        // Captured raw, redacted when stored.
        capture.ToApiCall().RequestBody.Should().NotContain("A01234567");
    }

    [Fact]
    public async Task An_error_status_is_recorded_and_returned_rather_than_thrown()
    {
        var stub = StubSupplierHandler.Answering(HttpStatusCode.InternalServerError, "<html>502</html>");
        using var client = ClientOver(stub);

        using var response = await client.SendAsync(IssueRequest("{}"));

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var capture = _recorder.Captures.Should().ContainSingle().Subject;
        capture.Outcome.Should().Be(SupplierCallOutcome.HttpError);
        capture.ResponseStatusCode.Should().Be(500);
        stub.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task A_timeout_is_recorded_as_an_unknown_outcome_and_the_call_is_never_sent_again()
    {
        // ADR-0003, the whole point: the issue call timed out, a ticket may exist, and a second
        // attempt would issue another one. One attempt, recorded as Timeout, surfaced as such.
        var stub = StubSupplierHandler.NeverAnswering();
        using var client = ClientOver(stub, timeout: TimeSpan.FromMilliseconds(100));

        var send = () => client.SendAsync(IssueRequest("{}"));

        (await send.Should().ThrowAsync<SupplierCallTimeoutException>())
            .Which.Message.Should().Contain("never by sending the call again");

        stub.Attempts.Should().Be(1);
        var capture = _recorder.Captures.Should().ContainSingle().Subject;
        capture.Outcome.Should().Be(SupplierCallOutcome.Timeout);
        capture.ResponseStatusCode.Should().BeNull("no response arrived");
        capture.LatencyMs.Should().BeGreaterThanOrEqualTo(90);
    }

    [Fact]
    public async Task A_caller_giving_up_is_recorded_as_cancelled_not_as_a_timeout()
    {
        var stub = StubSupplierHandler.NeverAnswering();
        using var client = ClientOver(stub, timeout: TimeSpan.FromMinutes(1));
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var send = () => client.SendAsync(IssueRequest("{}"), caller.Token);

        await send.Should().ThrowAsync<OperationCanceledException>();
        _recorder.Captures.Should().ContainSingle().Which.Outcome.Should().Be(SupplierCallOutcome.Cancelled);
        stub.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task A_transport_failure_is_recorded_with_its_error_and_rethrown_after_one_attempt()
    {
        var refused = new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused");
        var stub = new StubSupplierHandler((_, _) => throw refused);
        using var client = ClientOver(stub);

        var send = () => client.SendAsync(IssueRequest("{}"));

        (await send.Should().ThrowAsync<HttpRequestException>()).Which.Should().BeSameAs(refused);
        stub.Attempts.Should().Be(1);

        var capture = _recorder.Captures.Should().ContainSingle().Subject;
        capture.Outcome.Should().Be(SupplierCallOutcome.TransportError);
        capture.ErrorMessage.Should().Contain("ConnectionError").And.Contain("Connection refused");
    }

    [Fact]
    public async Task A_request_not_tagged_for_auditing_is_refused_before_it_is_sent()
    {
        var stub = StubSupplierHandler.Answering(HttpStatusCode.OK, "{}");
        using var client = ClientOver(stub);

        var send = () => client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "api/v2/ticketing/issue"));

        (await send.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("ForSupplierCall");
        stub.Attempts.Should().Be(0, "an unaudited supplier call never leaves the building");
        _recorder.Captures.Should().BeEmpty();
    }

    [Fact]
    public async Task Without_an_explicit_correlation_id_the_current_activity_is_used_as_the_API_does()
    {
        using var activity = new Activity("checkout").Start();
        var stub = StubSupplierHandler.Answering(HttpStatusCode.OK, "{}");
        using var client = ClientOver(stub);

        var request = new HttpRequestMessage(HttpMethod.Get, "api/v2/booking/status")
            .ForSupplierCall(SupplierId, SupplierOperation.Status, new SupplierCallContext(AgencyId));
        using var response = await client.SendAsync(request);

        _recorder.Captures.Should().ContainSingle().Which.CorrelationId.Should().Be(activity.Id);
    }

    private HttpClient ClientOver(HttpMessageHandler network, TimeSpan? timeout = null) =>
        new(new SupplierAuditHandler(_recorder, TimeProvider.System, timeout ?? TimeSpan.FromSeconds(30)) { InnerHandler = network })
        {
            BaseAddress = new Uri("https://supplier.test/"),
        };

    private static HttpRequestMessage IssueRequest(string body) =>
        new HttpRequestMessage(HttpMethod.Post, "api/v2/ticketing/issue?channel=web")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        }
        .ForSupplierCall(SupplierId, SupplierOperation.Issue, new SupplierCallContext(AgencyId, BookingId, "order-7f3a"));
}
