using System.Net;
using System.Text;
using FluentAssertions;
using TripsAgent.Application.Payments;
using TripsAgent.Domain.Common;
using TripsAgent.Integrations.Paystack;

namespace TripsAgent.UnitTests.Payments;

/// <summary>
/// The Paystack transfer adapter (issue 69, ADR-0008): an answer is recorded, an unanswered call is
/// an unknown outcome that throws, and nothing in between is ever read as a failure.
/// </summary>
public class PaystackTransferTests
{
    [Fact]
    public async Task A_timeout_is_an_unknown_outcome_and_the_call_is_made_exactly_once()
    {
        var handler = new CountingHandler(_ => throw new TaskCanceledException("HttpClient timeout"));
        var transfers = new PaystackBankTransfers(Client(handler));

        var send = () => transfers.InitiateTransferAsync("PO-1", "RCP_1", Money.FromMajor(10_000), "NGN", "Withdrawal");

        await send.Should().ThrowAsync<PaymentGatewayUnavailableException>();
        handler.Calls.Should().Be(1, "no retry of any kind sits between the adapter and the gateway");
    }

    [Fact]
    public async Task A_server_error_is_an_unknown_outcome_not_a_failure()
    {
        var transfers = new PaystackBankTransfers(Client(new CountingHandler(_ => Respond(HttpStatusCode.BadGateway, "{}"))));

        var send = () => transfers.InitiateTransferAsync("PO-2", "RCP_1", Money.FromMajor(10_000), "NGN", "Withdrawal");

        await send.Should().ThrowAsync<PaymentGatewayUnavailableException>();
    }

    [Fact]
    public async Task A_duplicate_reference_means_our_earlier_attempt_arrived_and_is_unknown_not_failed()
    {
        var transfers = new PaystackBankTransfers(Client(new CountingHandler(_ =>
            Respond(HttpStatusCode.BadRequest, """{"status":false,"message":"Duplicate Transfer Reference"}"""))));

        var transfer = await transfers.InitiateTransferAsync("PO-3", "RCP_1", Money.FromMajor(10_000), "NGN", "Withdrawal");

        transfer.Outcome.Should().Be(TransferOutcome.Unknown);
    }

    [Fact]
    public async Task An_insufficient_balance_is_a_refusal_the_platform_owns()
    {
        var transfers = new PaystackBankTransfers(Client(new CountingHandler(_ =>
            Respond(HttpStatusCode.BadRequest, """{"status":false,"message":"Your balance is not enough to fulfil this request"}"""))));

        var transfer = await transfers.InitiateTransferAsync("PO-4", "RCP_1", Money.FromMajor(10_000), "NGN", "Withdrawal");

        transfer.Outcome.Should().Be(TransferOutcome.Refused);
    }

    [Theory]
    [InlineData("success", TransferOutcome.Succeeded)]
    [InlineData("failed", TransferOutcome.Failed)]
    [InlineData("reversed", TransferOutcome.Reversed)]
    [InlineData("pending", TransferOutcome.Queued)]
    [InlineData("otp", TransferOutcome.Unknown)]
    [InlineData("something-new", TransferOutcome.Unknown)]
    public async Task Only_success_failed_and_reversed_are_answers(string status, TransferOutcome expected)
    {
        var transfers = new PaystackBankTransfers(Client(new CountingHandler(_ => Respond(
            HttpStatusCode.OK,
            "{\"status\":true,\"message\":\"ok\",\"data\":{\"status\":\"" + status + "\",\"transfer_code\":\"TRF_x\"}}"))));

        var transfer = await transfers.GetTransferAsync("PO-5");

        transfer.Outcome.Should().Be(expected);
    }

    [Fact]
    public async Task A_transfer_paystack_has_no_record_of_stays_unknown()
    {
        var transfers = new PaystackBankTransfers(Client(new CountingHandler(_ =>
            Respond(HttpStatusCode.NotFound, """{"status":false,"message":"Transfer not found"}"""))));

        (await transfers.GetTransferAsync("PO-6")).Outcome.Should().Be(TransferOutcome.Unknown);
    }

    [Fact]
    public async Task The_banks_name_comes_back_from_resolve_and_a_bad_number_is_not_found()
    {
        var resolved = await new PaystackBankTransfers(Client(new CountingHandler(_ => Respond(
                HttpStatusCode.OK, """{"status":true,"message":"ok","data":{"account_number":"0123456789","account_name":"LAGOS TRAVEL LTD"}}"""))))
            .ResolveAccountAsync("0123456789", "058");

        resolved.Resolved.Should().BeTrue();
        resolved.AccountName.Should().Be("LAGOS TRAVEL LTD");

        var missing = await new PaystackBankTransfers(Client(new CountingHandler(_ => Respond(
                HttpStatusCode.UnprocessableEntity, """{"status":false,"message":"Could not resolve account name"}"""))))
            .ResolveAccountAsync("0123456780", "058");

        missing.Outcome.Should().Be(BankAccountResolution.NotFound);
    }

    private static HttpClient Client(HttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("https://api.paystack.test/") };

    private static HttpResponseMessage Respond(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class CountingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(respond(request));
        }
    }
}
