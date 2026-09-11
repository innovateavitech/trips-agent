using System.Text.Json;
using FluentAssertions;
using TripsAgent.Domain.Suppliers;

namespace TripsAgent.UnitTests.Suppliers;

/// <summary>
/// What the supplier call log may keep of a request or response body. Issue #39: merchant keys,
/// bearer tokens and passport numbers are redacted before anything is stored.
/// </summary>
public class SupplierPayloadRedactionTests
{
    private const string PassportNumber = "A01234567";
    private const string MerchantKey = "fake.merchant.key.value";
    private const string BearerToken = "fake.bearer.token.value";

    private static readonly DateTimeOffset Now = new(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);

    private static readonly Dictionary<string, string> BusHeaders = new()
    {
        ["Authorization"] = $"Bearer {BearerToken}",
        ["MerchantKey"] = MerchantKey,
        ["MerchantCode"] = "ACCESS",
    };

    // ------------------------------------------------------------------ JSON bodies

    [Fact]
    public void A_booking_request_keeps_the_passengers_but_not_their_passport_numbers()
    {
        // The shape plan §2.7 stores: a DOCS record carrying a passport.
        var body = $$"""
            {
              "Passengers": [
                {
                  "FirstName": "Adaeze",
                  "LastName": "Okafor",
                  "PassengerDocuments": [
                    { "DocType": "DOCS", "InnerDocType": "PASSPORT", "DocNumber": "{{PassportNumber}}", "IssuingCountry": "NG" }
                  ]
                },
                {
                  "FirstName": "Tunde",
                  "PassengerDocuments": [
                    { "doc_type": "DOCO", "inner_doc_type": "VISA", "doc_number": "V7654321" }
                  ]
                }
              ]
            }
            """;

        var redacted = SupplierPayloadRedaction.RedactRequestBody(body, [])!;

        redacted.Should().NotContain(PassportNumber).And.NotContain("V7654321");

        // Still valid JSON, and still the evidence of who was booked on what document type.
        var passenger = JsonDocument.Parse(redacted).RootElement.GetProperty("Passengers")[0];
        passenger.GetProperty("FirstName").GetString().Should().Be("Adaeze");

        var document = passenger.GetProperty("PassengerDocuments")[0];
        document.GetProperty("DocType").GetString().Should().Be("DOCS");
        document.GetProperty("InnerDocType").GetString().Should().Be("PASSPORT");
        document.GetProperty("DocNumber").GetString().Should().Be(SupplierPayloadRedaction.RedactedValue);
    }

    [Fact]
    public void Inside_a_document_a_generically_named_number_is_still_redacted()
    {
        // "Number" says nothing by itself; being inside a travel document is what makes it sensitive.
        var body = $$"""{ "TravelDocument": { "Type": "PASSPORT", "Number": "{{PassportNumber}}" }, "Seat": { "Number": "12A" } }""";

        var redacted = JsonDocument.Parse(SupplierPayloadRedaction.RedactRequestBody(body, [])!).RootElement;

        redacted.GetProperty("TravelDocument").GetProperty("Number").GetString().Should().Be(SupplierPayloadRedaction.RedactedValue);
        redacted.GetProperty("TravelDocument").GetProperty("Type").GetString().Should().Be("PASSPORT");
        redacted.GetProperty("Seat").GetProperty("Number").GetString().Should().Be("12A", "a seat is not a document");
    }

    [Fact]
    public void A_sensitive_value_is_redacted_whatever_its_type()
    {
        var body = """{ "Pin": 1234, "Token": { "value": "abc", "expires": 3600 }, "Passport": ["A1", "A2"] }""";

        var redacted = JsonDocument.Parse(SupplierPayloadRedaction.RedactRequestBody(body, [])!).RootElement;

        redacted.GetProperty("Pin").GetString().Should().Be(SupplierPayloadRedaction.RedactedValue);
        redacted.GetProperty("Token").GetString().Should().Be(SupplierPayloadRedaction.RedactedValue);
        redacted.GetProperty("Passport").GetString().Should().Be(SupplierPayloadRedaction.RedactedValue);
    }

    [Theory]
    [InlineData("DocNumber", true)]
    [InlineData("doc_number", true)]
    [InlineData("PASSPORT_NUMBER", true)]
    [InlineData("passportNo", true)]
    [InlineData("MerchantKey", true)]
    [InlineData("access_token", true)]
    [InlineData("client_secret", true)]
    [InlineData("Password", true)]
    [InlineData("cardNumber", true)]
    [InlineData("CVV", true)]
    [InlineData("Hash", true)]
    [InlineData("pin", true)]
    [InlineData("FareKey", false)]
    [InlineData("OfferKey", false)]
    [InlineData("StoppingPoints", false)]
    [InlineData("Company", false)]
    [InlineData("MerchantCode", false)]
    [InlineData("DocType", false)]
    [InlineData("ConfirmationCode", false)]
    public void Sensitive_properties_are_recognised_by_name(string name, bool sensitive) =>
        SupplierPayloadRedaction.IsSensitiveProperty(name).Should().Be(sensitive);

    [Fact]
    public void A_body_with_nothing_to_redact_is_stored_exactly_as_it_was()
    {
        // Byte for byte — whitespace, key order and the '+' of a phone number — because this is evidence.
        const string body = "{\n  \"Phone\": \"+2348012345678\",\n  \"Origin\": \"LOS\", \"Destination\": \"ABV\"\n}";

        SupplierPayloadRedaction.RedactRequestBody(body, []).Should().Be(body);
        SupplierPayloadRedaction.RedactResponseBody(body, []).Should().Be(body);
    }

    // ------------------------------------------------------------------ bodies that are not JSON

    [Fact]
    public void A_request_body_that_is_not_JSON_is_withheld_rather_than_guessed_at()
    {
        var body = $"passport={PassportNumber}&merchantKey=abc";

        var stored = SupplierPayloadRedaction.RedactRequestBody(body, []);

        stored.Should().NotContain(PassportNumber).And.StartWith("[withheld").And.Contain($"{body.Length} characters");
    }

    [Fact]
    public void An_HTML_error_page_is_kept_as_evidence_with_any_echoed_credential_removed()
    {
        var page = $"<html><body><h1>502 Bad Gateway</h1><p>upstream rejected key {MerchantKey}</p></body></html>";

        var stored = SupplierPayloadRedaction.RedactResponseBody(page, SupplierPayloadRedaction.KnownSecretsFrom(BusHeaders));

        stored.Should().Contain("502 Bad Gateway").And.NotContain(MerchantKey).And.Contain(SupplierPayloadRedaction.RedactedValue);
    }

    [Fact]
    public void A_credential_echoed_under_an_innocent_name_is_still_removed()
    {
        // The name says nothing, but the value is our bearer token — without its "Bearer " scheme.
        var body = $$"""{ "Debug": { "ReceivedAuth": "{{BearerToken}}" }, "Status": 0 }""";

        var stored = SupplierPayloadRedaction.RedactResponseBody(body, SupplierPayloadRedaction.KnownSecretsFrom(BusHeaders));

        stored.Should().NotContain(BearerToken).And.Contain("\"Status\": 0");
    }

    [Fact]
    public void A_short_header_value_is_not_scrubbed_from_bodies_by_value()
    {
        // MerchantCode is not a secret, and "ACCESS" is too short and too common to hunt for anyway.
        var secrets = SupplierPayloadRedaction.KnownSecretsFrom(new Dictionary<string, string> { ["X-Api-Key"] = "abc" });

        secrets.Should().BeEmpty();
    }

    // ------------------------------------------------------------------ the query string

    [Fact]
    public void A_sensitive_query_parameter_is_redacted_and_the_rest_kept()
    {
        var endpoint = SupplierPayloadRedaction.RedactEndpoint("/api/v2/booking/status?bookingRef=TRP123&access_token=xyz");

        endpoint.Should().Be("/api/v2/booking/status?bookingRef=TRP123&access_token=[redacted]");
    }

    // ------------------------------------------------------------------ through the factory

    [Fact]
    public void Recording_a_call_redacts_both_bodies_and_the_endpoint_so_no_caller_can_forget()
    {
        var id = Guid.CreateVersion7();

        var call = SupplierApiCall.Record(
            supplierId: Guid.CreateVersion7(),
            agencyId: Guid.CreateVersion7(),
            supplierBookingId: Guid.CreateVersion7(),
            operation: SupplierOperation.Issue,
            httpMethod: "POST",
            endpoint: "/api/v2/ticketing/issue?token=abcdef",
            requestHeaders: BusHeaders,
            requestBody: $$"""{ "DocNumber": "{{PassportNumber}}" }""",
            responseStatusCode: 500,
            responseBody: $"<pre>{MerchantKey}</pre>",
            latencyMs: 30_000,
            outcome: SupplierCallOutcome.HttpError,
            occurredAt: Now,
            errorMessage: new string('x', 5_000),
            correlationId: new string('c', 300),
            id: id);

        call.Id.Should().Be(id, "the handler chose it so a status poll can point at this call");
        call.RequestBody.Should().NotContain(PassportNumber);
        call.ResponseBody.Should().NotContain(MerchantKey);
        call.Endpoint.Should().NotContain("abcdef");

        // Shortened to the column widths here, so an overlong value cannot fail the whole insert.
        call.ErrorMessage.Should().HaveLength(SupplierApiCall.ErrorMessageMaxLength);
        call.CorrelationId.Should().HaveLength(SupplierApiCall.CorrelationIdMaxLength);
    }
}
