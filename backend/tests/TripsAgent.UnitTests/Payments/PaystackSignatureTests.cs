using System.Net;
using System.Text;
using FluentAssertions;
using TripsAgent.Domain.Common;
using TripsAgent.Integrations.Paystack;

namespace TripsAgent.UnitTests.Payments;

/// <summary>
/// The signature check, which is the only thing standing between a public URL and someone
/// crediting their own wallet.
/// </summary>
/// <remarks>
/// The expected signature below is a known-answer vector, computed independently rather than by
/// calling the same code the test is checking. A test that recomputes the HMAC with the same
/// primitive would pass just as happily against SHA-256, the wrong key order, or a digest over
/// the parsed body instead of the raw bytes.
/// </remarks>
public class PaystackSignatureTests
{
    /// <summary>
    /// Not a real key, and deliberately not shaped like one — a literal beginning <c>sk_test_</c>
    /// would trip the pre-commit secret scanner for every developer who touched this file.
    /// </summary>
    private const string Key = "paystack-webhook-test-key";

    private const string Body =
        """{"event":"charge.success","data":{"id":4099260516,"reference":"TA-TEST-001","amount":500000,"fees":7500,"currency":"NGN","status":"success","gateway_response":"Successful"}}""";

    /// <summary>HMAC-SHA512 of <see cref="Body"/> keyed by <see cref="Key"/>, as hex.</summary>
    private const string ValidSignature =
        "ee4b3470559cd72e048d99640296432b2e2fdb7f3b68d894ad7eb74f9f1c7a97"
        + "acc468ade5d3853330673d86517baef4c450370fda8e41ab865bcb8b507b7f27";

    private static readonly System.Text.Json.JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static PaystackGateway Gateway(string secretKey = Key) =>
        new(new HttpClient(new UnreachableHandler()) { BaseAddress = new Uri("https://api.paystack.test") },
            new PaystackOptions { SecretKey = secretKey });

    [Fact]
    public void The_documented_signature_is_accepted() =>
        Gateway().IsValidSignature(Body, ValidSignature).Should().BeTrue();

    [Fact]
    public void Paystack_sends_lowercase_hex_but_uppercase_is_accepted_too() =>
        Gateway().IsValidSignature(Body, ValidSignature.ToUpperInvariant()).Should().BeTrue();

    [Fact]
    public void Surrounding_whitespace_does_not_break_it() =>
        Gateway().IsValidSignature(Body, $"  {ValidSignature}\t").Should().BeTrue();

    [Fact]
    public void A_missing_signature_is_rejected()
    {
        Gateway().IsValidSignature(Body, null).Should().BeFalse();
        Gateway().IsValidSignature(Body, string.Empty).Should().BeFalse();
        Gateway().IsValidSignature(Body, "   ").Should().BeFalse();
    }

    [Fact]
    public void A_body_changed_by_one_character_is_rejected()
    {
        // The amount, raised from ₦5,000.00 to ₦50,000.00 by adding a zero — the whole point of
        // signing the body. The signature is still a genuine one, for different bytes.
        var tampered = Body.Replace("\"amount\":500000", "\"amount\":5000000", StringComparison.Ordinal);

        tampered.Should().NotBe(Body);
        Gateway().IsValidSignature(tampered, ValidSignature).Should().BeFalse();
    }

    [Fact]
    public void A_signature_from_a_different_key_is_rejected() =>
        Gateway("a-different-key").IsValidSignature(Body, ValidSignature).Should().BeFalse();

    [Fact]
    public void A_truncated_signature_is_rejected() =>
        Gateway().IsValidSignature(Body, ValidSignature[..64]).Should().BeFalse();

    [Fact]
    public void Nonsense_is_rejected_rather_than_throwing() =>
        Gateway().IsValidSignature(Body, "not-hex-at-all").Should().BeFalse();

    [Fact]
    public void With_no_key_configured_nothing_verifies()
    {
        // A gateway with no secret must fail closed. Failing open here would accept every forged
        // webhook on any environment where the key was forgotten.
        Gateway(string.Empty).IsValidSignature(Body, ValidSignature).Should().BeFalse();
    }

    [Fact]
    public void Reformatting_the_body_breaks_the_signature()
    {
        // Recorded because it explains a design decision that otherwise looks fussy: the endpoint
        // reads raw bytes instead of model-binding. Parse and re-serialise, and this is what
        // happens to the signature.
        var reformatted = System.Text.Json.JsonSerializer.Serialize(
            System.Text.Json.JsonDocument.Parse(Body).RootElement,
            Indented);

        // Same data, different bytes — which is all HMAC cares about.
        reformatted.Should().NotBe(Body);
        Gateway().IsValidSignature(reformatted, ValidSignature).Should().BeFalse();
    }

    [Fact]
    public void An_empty_body_with_a_borrowed_signature_is_rejected() =>
        Gateway().IsValidSignature(string.Empty, ValidSignature).Should().BeFalse();

    /// <summary>
    /// Money crosses to Paystack in minor units with no conversion, so a kobo amount stays a
    /// kobo amount.
    /// </summary>
    [Fact]
    public void Naira_amounts_are_kobo_all_the_way_out()
    {
        // ₦5,000.00. Paystack's "subunit of the supported currency" is exactly what Money holds,
        // which is why nothing in the gateway multiplies or divides by 100.
        Money.FromMajor(5_000).AmountMinor.Should().Be(500_000);

        // And the sample in Paystack's own docs: ₦403.33 paid, fee ₦102.83.
        new Money(40_333).ToString().Should().Be("403.33");
        new Money(10_283).ToString().Should().Be("102.83");
    }

    /// <summary>Fails any call, so a signature test can never accidentally reach the network.</summary>
    private sealed class UnreachableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A signature check must not make an HTTP call.");
    }
}
