using System.Text;
using System.Text.Json;
using FluentAssertions;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Suppliers;

namespace TripsAgent.UnitTests.Suppliers;

/// <summary>
/// The smaller supplier rules: what a call log may keep, what a credential may hold, and what a
/// search result must look like before it is stored.
/// </summary>
public class SupplierDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);

    // ------------------------------------------------------------------ the call log

    [Fact]
    public void A_recorded_call_never_keeps_a_credential_header()
    {
        // Trips Africa's bus endpoints send the merchant key itself in a MerchantKey header, and a
        // hash of it as the bearer token. The call log keeps every request for months.
        var call = SupplierApiCall.Record(
            supplierId: Guid.CreateVersion7(),
            agencyId: Guid.CreateVersion7(),
            supplierBookingId: null,
            operation: SupplierOperation.Search,
            httpMethod: "post",
            endpoint: "/api/Bus/SearchBus",
            requestHeaders: new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer hash-of-the-merchant-key",
                ["MerchantKey"] = "the-merchant-key-itself",
                ["X-Api-Key"] = "another-supplier-api-key",
                ["x-auth-token"] = "yet-another-token",
                ["MerchantCode"] = "ACCESS",
                ["Content-Type"] = "application/json",
            },
            requestBody: "{}",
            responseStatusCode: 200,
            responseBody: "{}",
            latencyMs: 812,
            outcome: SupplierCallOutcome.Succeeded,
            occurredAt: Now);

        call.RequestHeaders.Should().NotContain("hash-of-the-merchant-key")
            .And.NotContain("the-merchant-key-itself")
            .And.NotContain("another-supplier-api-key")
            .And.NotContain("yet-another-token");

        var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(call.RequestHeaders)!;

        headers["Authorization"].Should().Be(SupplierApiCall.RedactedValue);
        headers["MerchantKey"].Should().Be(SupplierApiCall.RedactedValue);

        // Not everything is redacted: the merchant code is sent in clear and is what support needs.
        headers["MerchantCode"].Should().Be("ACCESS");
        headers["Content-Type"].Should().Be("application/json");
        call.HttpMethod.Should().Be("POST");
    }

    [Theory]
    [InlineData("Authorization", true)]
    [InlineData("merchantkey", true)]
    [InlineData("X-Signature", true)]
    [InlineData("Cookie", true)]
    [InlineData("MerchantCode", false)]
    [InlineData("Accept", false)]
    public void Sensitive_headers_are_recognised_by_name(string header, bool sensitive) =>
        SupplierApiCall.IsSensitiveHeader(header).Should().Be(sensitive);

    // ------------------------------------------------------------------ suppliers and credentials

    [Theory]
    [InlineData("trips_africa")]
    [InlineData("acme2")]
    public void A_supplier_code_is_lower_snake_case(string code) =>
        Supplier.Register(code, "Name", SupplierKind.Multi, "https://example.test").Code.Should().Be(code);

    [Theory]
    [InlineData("Trips Africa")]
    [InlineData("TRIPS_AFRICA")]
    [InlineData("2trips")]
    [InlineData("trips-africa")]
    public void A_malformed_supplier_code_is_refused(string code)
    {
        var register = () => Supplier.Register(code, "Name", SupplierKind.Multi, "https://example.test");

        register.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_null_agency_is_a_platform_credential_and_an_empty_one_is_refused()
    {
        var supplierId = Guid.CreateVersion7();

        SupplierCredential.Create(supplierId, null, SupplierEnvironment.Staging, "ACCESS", [1, 2, 3], null, Now)
            .IsPlatformLevel.Should().BeTrue();

        SupplierCredential.Create(supplierId, Guid.CreateVersion7(), SupplierEnvironment.Staging, "ACCESS", [1, 2, 3], null, Now)
            .IsPlatformLevel.Should().BeFalse();

        // Guid.Empty is neither an agency nor the platform — usually an unset variable.
        var empty = () => SupplierCredential.Create(supplierId, Guid.Empty, SupplierEnvironment.Staging, "ACCESS", [1], null, Now);
        empty.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_credential_refuses_an_empty_secret()
    {
        var supplierId = Guid.CreateVersion7();

        var noKey = () => SupplierCredential.Create(supplierId, null, SupplierEnvironment.Staging, "ACCESS", [], null, Now);
        var emptyToken = () => SupplierCredential.Create(supplierId, null, SupplierEnvironment.Staging, "ACCESS", [1], [], Now);

        noKey.Should().Throw<ArgumentException>();
        emptyToken.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rotating_a_credential_replaces_both_secrets_and_records_when()
    {
        var credential = SupplierCredential.Create(Guid.CreateVersion7(), null, SupplierEnvironment.Staging, "ACCESS", [1], [2], Now);

        credential.Rotate("ACCESS2", [9, 9], null, Now.AddDays(30));

        credential.MerchantCode.Should().Be("ACCESS2");
        credential.MerchantKeyEncrypted.Should().Equal(9, 9);
        credential.BearerTokenEncrypted.Should().BeNull();
        credential.RotatedAt.Should().Be(Now.AddDays(30));
    }

    [Fact]
    public void A_credential_describes_itself_without_its_secrets()
    {
        var key = Encoding.UTF8.GetBytes("ciphertext-bytes");
        var credential = SupplierCredential.Create(Guid.CreateVersion7(), null, SupplierEnvironment.Production, "ACCESS", key, null, Now);

        credential.ToString().Should().Contain("platform").And.NotContain("ciphertext")
            .And.NotContain(Convert.ToBase64String(key));
    }

    // ------------------------------------------------------------------ search results

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855")]
    public void A_search_cache_key_must_be_a_lower_case_sha256(string hash)
    {
        var start = () => SearchRequest.Start(Guid.CreateVersion7(), null, SupplierProductType.Flight, hash, "{}", "OneWay", Now);

        start.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void An_offer_keeps_the_suppliers_identity_tuple_exactly()
    {
        var reference = new SupplierOfferReference("agent-7", "gds-1", "comb-42", "rec-9", 2);

        var offer = SupplierOffer.Record(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), SupplierProductType.Flight,
            "offer-ref", reference, "ngn", new Money(80_000_00), new Money(95_000_00), "{}", Now, null);

        offer.Reference.Should().Be(reference);
        offer.Currency.Should().Be("NGN");
    }

    [Fact]
    public void An_offer_from_an_aggregator_with_a_single_token_needs_no_tuple()
    {
        // The shape a second aggregator would most likely use: one opaque handle, nothing else.
        var offer = SupplierOffer.Record(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), SupplierProductType.Bus,
            "opaque-token", new SupplierOfferReference(), "NGN", new Money(10_000_00), new Money(12_000_00), "{}", Now, null);

        offer.Reference.Should().Be(new SupplierOfferReference());
    }

    [Fact]
    public void An_offer_cannot_carry_a_negative_fare()
    {
        var record = () => SupplierOffer.Record(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), SupplierProductType.Flight,
            "offer-ref", new SupplierOfferReference(), "NGN", new Money(-1), new Money(10), "{}", Now, null);

        record.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("LO")]
    [InlineData("LOSX")]
    [InlineData("L0S")]
    public void A_flight_segment_needs_real_airport_codes(string origin)
    {
        var create = () => FlightSegment.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), 0, 0, "P4", null, "7120", origin, "ABV", Now, Now.AddHours(1));

        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_flight_cannot_arrive_before_it_departs()
    {
        var create = () => FlightSegment.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), 0, 0, "P4", null, "7120", "LOS", "ABV", Now, Now.AddMinutes(-1));

        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_travel_document_holds_only_ciphertext_and_sane_dates()
    {
        var expiresBeforeIssued = () => PassengerDocument.Add(
            Guid.CreateVersion7(), Guid.CreateVersion7(), TravelDocumentRecord.Docs, TravelDocumentKind.Passport,
            [1, 2, 3], "NG", issuedOn: new DateOnly(2030, 1, 1), expiresOn: new DateOnly(2020, 1, 1));

        var noCiphertext = () => PassengerDocument.Add(
            Guid.CreateVersion7(), Guid.CreateVersion7(), TravelDocumentRecord.Docs, TravelDocumentKind.Passport, [], "NG");

        var badCountry = () => PassengerDocument.Add(
            Guid.CreateVersion7(), Guid.CreateVersion7(), TravelDocumentRecord.Docs, TravelDocumentKind.Passport, [1], "NGA");

        expiresBeforeIssued.Should().Throw<ArgumentException>();
        noCiphertext.Should().Throw<ArgumentException>();
        badCountry.Should().Throw<ArgumentException>();
    }
}
