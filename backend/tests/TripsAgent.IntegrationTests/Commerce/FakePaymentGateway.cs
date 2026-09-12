using System.Collections.Concurrent;
using TripsAgent.Application.Payments;
using TripsAgent.Application.Storefront;
using TripsAgent.Domain.Common;

namespace TripsAgent.IntegrationTests.Commerce;

/// <summary>
/// A payment gateway that answers from memory instead of from a card network.
/// </summary>
/// <remarks>
/// <para>
/// Everything a test needs to steer: what <see cref="VerifyAsync"/> says about a payment, and what
/// <see cref="RefundAsync"/> answers. It records every call, so a test can assert that a refund was
/// sent once and for the right amount — which is the part that matters.
/// </para>
/// <para>
/// It starts payments as <b>pending</b>, like the real thing: a payment only succeeds because a test
/// says so, never because it was initialised. That keeps the tests honest about the one rule the
/// money path rests on — nothing is credited until the gateway is asked and answers.
/// </para>
/// </remarks>
public sealed class FakePaymentGateway : IPaymentGateway
{
    private readonly ConcurrentDictionary<string, GatewayVerification> _answers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Money> _requested = new(StringComparer.Ordinal);

    /// <summary>Every refund asked for, newest last. What a test asserts about.</summary>
    public List<(string Reference, Money Amount, string Reason)> Refunds { get; } = [];

    /// <summary>What every refund answers with. Accepted unless a test says otherwise.</summary>
    public GatewayRefund RefundAnswer { get; set; } =
        new(GatewayRefundOutcome.Accepted, "pending", "fake-refund-1", null);

    /// <summary>Thrown by <see cref="RefundAsync"/> when set: the gateway could not be reached.</summary>
    public Exception? RefundThrows { get; set; }

    public string Name => "fake";

    /// <summary>Says the payer paid exactly what was asked for. What a test calls after checkout.</summary>
    public void Succeed(string reference, Money? paid = null, string currency = "NGN") =>
        _answers[reference] = new GatewayVerification(
            GatewayPaymentOutcome.Succeeded,
            "success",
            paid ?? _requested.GetValueOrDefault(reference),
            Money.Zero,
            currency,
            $"gw-{reference}",
            null);

    /// <summary>Says the payer's card was declined.</summary>
    public void Fail(string reference, string reason = "declined") =>
        _answers[reference] = new GatewayVerification(
            GatewayPaymentOutcome.Failed, "failed", Money.Zero, Money.Zero, "NGN", $"gw-{reference}", reason);

    /// <summary>What the checkout asked the payer for.</summary>
    public Money RequestedFor(string reference) => _requested.GetValueOrDefault(reference);

    public Task<GatewayInitialization> InitializeAsync(
        string reference,
        Money amount,
        string currency,
        string customerEmail,
        string callbackUrl,
        CancellationToken cancellationToken = default)
    {
        _requested[reference] = amount;

        return Task.FromResult(new GatewayInitialization($"https://pay.test/{reference}", $"gw-{reference}"));
    }

    public Task<GatewayVerification> VerifyAsync(string reference, CancellationToken cancellationToken = default) =>
        Task.FromResult(_answers.TryGetValue(reference, out var answer)
            ? answer
            : new GatewayVerification(
                GatewayPaymentOutcome.Pending, "abandoned", Money.Zero, Money.Zero, "NGN", $"gw-{reference}", null));

    public Task<GatewayRefund> RefundAsync(
        string reference,
        Money amount,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (RefundThrows is { } thrown)
        {
            return Task.FromException<GatewayRefund>(thrown);
        }

        lock (Refunds)
        {
            Refunds.Add((reference, amount, reason));
        }

        return Task.FromResult(RefundAnswer);
    }

    public bool IsValidSignature(string payload, string? signature) => true;
}

/// <summary>
/// One agency's storefront, answering on one host name.
/// </summary>
/// <remarks>
/// Stands in for <c>SiteDomainDirectory</c>, which reads the agency's verified domains. What the
/// tests are about is what happens after a host resolves, so resolving it is fixed rather than
/// seeded — and a host that is not this one resolves to nobody, which is the case the 404s rest on.
/// </remarks>
public sealed class FixedStorefrontDirectory : IStorefrontDirectory
{
    /// <summary>The host every test uses for the agency's shop.</summary>
    public const string Host = "lagos-travel.test";

    private readonly Guid _agencyId;

    public FixedStorefrontDirectory(Guid agencyId) => _agencyId = agencyId;

    public Task<Guid?> FindAgencyAsync(string host, CancellationToken cancellationToken = default) =>
        Task.FromResult<Guid?>(string.Equals(host, Host, StringComparison.OrdinalIgnoreCase) ? _agencyId : null);

    public Task<Uri> SiteUrlAsync(Guid agencyId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new Uri($"https://{Host}"));
}
