using System.Net;
using System.Text;
using FluentAssertions;
using TripsAgent.Application.Payments;
using TripsAgent.Domain.Common;
using TripsAgent.Integrations.Paystack;

namespace TripsAgent.UnitTests.Payments;

/// <summary>
/// Sending a traveller's money back to the card it came from (build plan F5, moved here from issue 43).
/// </summary>
/// <remarks>
/// The distinction every test here is about: a refusal is an <i>answer</i> and is recorded, while a
/// gateway that could not be reached is an <i>unknown</i> and throws — because a refund nobody
/// confirmed must never be written down as one that happened.
/// </remarks>
public class PaystackRefundTests
{
    private const string Key = "paystack-refund-test-key";

    [Fact]
    public async Task An_accepted_refund_carries_the_gateways_own_reference()
    {
        var gateway = GatewayReturning(
            HttpStatusCode.OK,
            """{"status":true,"message":"Refund has been queued","data":{"id":8765,"status":"pending"}}""");

        var refund = await gateway.RefundAsync("TA-ref-1", Money.FromMajor(15_000), "Booking ORD-2026-000142 — refund");

        refund.Outcome.Should().Be(GatewayRefundOutcome.Accepted);
        refund.Accepted.Should().BeTrue();

        // Kept for reconciliation: a settlement export names the refund by this id, not by ours.
        refund.GatewayRefundReference.Should().Be("8765");
        refund.Status.Should().Be("pending");
    }

    [Fact]
    public async Task Refunding_twice_reads_as_already_refunded_rather_than_as_a_failure()
    {
        // A redelivered reversal message, or a reversal racing an agent's own refund. The second
        // request must be harmless, and must not raise an alarm about money that did go back.
        var gateway = GatewayReturning(
            HttpStatusCode.BadRequest,
            """{"status":false,"message":"Transaction has already been fully refunded"}""");

        var refund = await gateway.RefundAsync("TA-ref-2", Money.FromMajor(15_000), "reason");

        refund.Outcome.Should().Be(GatewayRefundOutcome.AlreadyRefunded);
        refund.Accepted.Should().BeTrue();
    }

    [Fact]
    public async Task A_refusal_is_reported_with_paystacks_own_explanation_and_sends_nothing()
    {
        var gateway = GatewayReturning(
            HttpStatusCode.BadRequest,
            """{"status":false,"message":"Transaction is too old to refund"}""");

        var refund = await gateway.RefundAsync("TA-ref-3", Money.FromMajor(15_000), "reason");

        refund.Outcome.Should().Be(GatewayRefundOutcome.Refused);
        refund.Accepted.Should().BeFalse();

        // The message a person can act on, rather than "the payment provider is not responding".
        refund.FailureReason.Should().Contain("too old to refund");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task A_gateway_that_could_not_answer_is_an_unknown_outcome_and_throws(HttpStatusCode status)
    {
        var gateway = GatewayReturning(status, """{"status":false,"message":"try again"}""");

        var act = () => gateway.RefundAsync("TA-ref-4", Money.FromMajor(15_000), "reason");

        // Not a refusal. The caller retries; nothing is recorded as refunded on the strength of this.
        await act.Should().ThrowAsync<PaymentGatewayUnavailableException>();
    }

    [Fact]
    public async Task A_refund_of_nothing_is_refused_before_the_gateway_is_asked()
    {
        var gateway = GatewayReturning(HttpStatusCode.OK, """{"status":true,"data":{"id":1,"status":"pending"}}""");

        var act = () => gateway.RefundAsync("TA-ref-5", Money.Zero, "reason");

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task The_request_names_the_original_payment_and_carries_no_card_detail()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.OK,
            """{"status":true,"data":{"id":42,"status":"pending"}}""");

        var gateway = new PaystackGateway(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.paystack.test") },
            new PaystackOptions { SecretKey = Key });

        await gateway.RefundAsync("TA-ref-6", Money.FromMajor(1_500), "Booking ORD-2026-000142 — refund");

        handler.Path.Should().Be("/refund");

        // Minor units, as a string: the same contract initialise sends an amount under.
        handler.Body.Should().Contain("\"transaction\":\"TA-ref-6\"");
        handler.Body.Should().Contain("\"amount\":\"150000\"");

        // Nothing that could be a card. The gateway knows where the money came from; we never do.
        handler.Body.Should().NotContain("card");
        handler.Body.Should().NotContain("authorization");
    }

    private static PaystackGateway GatewayReturning(HttpStatusCode status, string body) =>
        new(
            new HttpClient(new RecordingHandler(status, body)) { BaseAddress = new Uri("https://api.paystack.test") },
            new PaystackOptions { SecretKey = Key });

    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? Path { get; private set; }

        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Path = request.RequestUri?.AbsolutePath;
            Body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
