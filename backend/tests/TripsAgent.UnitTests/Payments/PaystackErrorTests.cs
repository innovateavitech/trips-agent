using System.Net;
using System.Text;
using FluentAssertions;
using TripsAgent.Application.Payments;
using TripsAgent.Domain.Common;
using TripsAgent.Integrations.Paystack;

namespace TripsAgent.UnitTests.Payments;

/// <summary>
/// What the adapter says when Paystack refuses a request.
/// </summary>
/// <remarks>
/// This mattered in practice: a seeded demo agency whose email ended in <c>.test</c> made Paystack
/// answer <c>"email" must be a valid email</c>, and the console reported that the payment provider
/// was not responding. The provider answered immediately and precisely; we threw the answer away.
/// </remarks>
public class PaystackErrorTests
{
    private const string Key = "paystack-error-test-key";

    [Fact]
    public async Task A_refusal_carries_paystacks_own_explanation()
    {
        var gateway = GatewayReturning(
            HttpStatusCode.BadRequest,
            """{"status":false,"message":"\"email\" must be a valid email"}""");

        var act = () => gateway.InitializeAsync(
            "TA-ref-1", Money.FromMajor(5_000), "NGN", "owner@agency.test", "https://console.test/return");

        var thrown = await act.Should().ThrowAsync<PaymentGatewayException>();

        // The message a person can act on, and the status that says who is at fault.
        thrown.Which.Message.Should().Contain("must be a valid email");
        thrown.Which.Message.Should().Contain("400");
        thrown.Which.Message.Should().Contain("TA-ref-1");
    }

    [Fact]
    public async Task An_unreadable_refusal_still_names_the_status_rather_than_throwing_something_else()
    {
        var gateway = GatewayReturning(HttpStatusCode.BadGateway, "<html>gateway timeout</html>");

        var act = () => gateway.InitializeAsync(
            "TA-ref-2", Money.FromMajor(5_000), "NGN", "owner@agency.example.com", "https://console.test/return");

        (await act.Should().ThrowAsync<PaymentGatewayException>())
            .Which.Message.Should().Contain("502");
    }

    private static PaystackGateway GatewayReturning(HttpStatusCode status, string body) =>
        new(
            new HttpClient(new StubHandler(status, body)) { BaseAddress = new Uri("https://api.paystack.test") },
            new PaystackOptions { SecretKey = Key });

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
